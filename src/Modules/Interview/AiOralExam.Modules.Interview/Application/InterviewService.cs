using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Application.Events;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview.Application;

/// <summary>
/// Orchestrates the exam flow:
///   Start  -> create InterviewAttempt + QuestionResponse for question 1 + a Main turn
///   Submit -> store the transcript -> analyze (IAnswerAnalyzer) -> follow-up OR score (IRubricScorer)
///          -> next question OR finish the attempt (PendingReview)
/// The received transcript is persisted before the provider call. Evaluation and
/// state transitions are saved afterwards so a provider failure never discards
/// the answer and can be retried.
/// </summary>
internal sealed class InterviewService(
    InterviewDbContext db,
    IExamConfigurationApi examConfig,
    IAnswerAnalyzer analyzer,
    IRubricScorer scorer,
    IInterviewEventPublisher eventPublisher,
    ICurrentUser currentUser,
    TimeProvider clock,
    IOptions<InterviewOptions> options,
    ILogger<InterviewService> logger) : IInterviewService
{
    private readonly InterviewOptions _options = options.Value;

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<MySessionDto>> GetMySessionsAsync(CancellationToken ct = default)
    {
        var me = currentUser.Id;
        var participants = await db.Participants.AsNoTracking()
            .Where(p => p.StudentId == me)
            .Include(p => p.Attempts)
            .ToListAsync(ct);

        var sessions = (await examConfig.GetSessionsAsync(participants.Select(p => p.ExamSessionId).ToList(), ct))
            .Where(s => s.Status != ExamSessionStatus.Draft)   // students never see draft sessions
            .ToDictionary(s => s.Id);

        var now = UtcNow;
        return participants
            .Where(p => sessions.ContainsKey(p.ExamSessionId))
            .Select(p =>
            {
                var s = sessions[p.ExamSessionId];
                var latest = p.Attempts.MaxBy(a => a.StartedAt);
                return new MySessionDto(
                    s.Id, s.CourseCode, s.Title, s.StartTime, s.EndTime, s.Status.ToString(), s.IsOpenAt(now),
                    s.Questions.Count, s.TimeLimitPerAnswerSeconds, p.Status.ToString(),
                    latest?.Id, latest?.Status.ToString());
            })
            .OrderBy(x => x.StartTime)
            .ToList();
    }

    // ------------------------------------------------------------------
    public async Task<(AttemptStateDto State, bool Created)> StartAttemptAsync(StartAttemptRequest request, CancellationToken ct = default)
    {
        if (request.ExamSessionId == Guid.Empty)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["examSessionId"] = ["examSessionId is required."]
            });

        var me = currentUser.Id;
        var session = await examConfig.GetSessionAsync(request.ExamSessionId, ct)
                      ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");

        var participantId = await db.Participants.AsNoTracking()
            .Where(p => p.ExamSessionId == session.Id && p.StudentId == me)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new ForbiddenException("You are not registered for this exam session.");

        var events = new List<InterviewAttemptEvent>();
        var result = await WithParticipantLockAsync(participantId, async () =>
        {
            var participant = await db.Participants
                                  .Include(p => p.Attempts).ThenInclude(a => a.Responses).ThenInclude(r => r.Turns)
                                  .FirstOrDefaultAsync(p => p.Id == participantId, ct)
                              ?? throw new ForbiddenException("You are not registered for this exam session.");

            var now = UtcNow;
            var running = participant.Attempts.FirstOrDefault(a => a.Status == AttemptStatus.InProgress);
            if (running is not null)
            {
                var activeTurn = running.CurrentResponse?.CurrentTurn;
                if (!session.IsOpenAt(now) || activeTurn is null || now > GetTurnDeadline(activeTurn, session))
                    throw new ConflictException("attempt_deadline_passed", "The active attempt is no longer eligible to resume.");
                events.Add(CreateEvent(running, session.Id, "attempt.resumed", InterviewEventSequence.Current(running), now));
                return (ToState(running, session), false);
            }

            if (!session.IsOpenAt(now))
                throw new ConflictException("exam_session_not_open",
                    session.Status != ExamSessionStatus.Published
                        ? "The exam session is not open."
                        : $"The exam session is open only from {session.StartTime:u} to {session.EndTime:u} (UTC).");

            if (participant.Attempts.Count > 0 && !_options.AllowRetake)
                throw new ConflictException("attempt_already_taken", "You have already taken this exam session.");

            if (session.Questions.Count == 0)
                throw new ConflictException("exam_session_has_no_questions", "The exam session has no questions.");

            var attempt = new InterviewAttempt
            {
                Id = Guid.NewGuid(),
                ParticipantId = participant.Id,
                StartedAt = now,
            };
            participant.Attempts.Add(attempt);
            db.InterviewAttempts.Add(attempt);
            participant.Status = ParticipantStatus.InProgress;
            AskMainQuestion(attempt, session.Questions[0], now);
            events.Add(CreateEvent(attempt, session.Id, "attempt.started", InterviewEventSequence.AttemptStarted, now));
            events.Add(CreateEvent(attempt, session.Id, "turn.opened", InterviewEventSequence.TurnOpened(1), now));

            logger.LogInformation("Attempt {AttemptId} started by {StudentId} for session {SessionId}",
                attempt.Id, me, session.Id);
            return (ToState(attempt, session), true);
        }, ct);

        await PublishEventsAsync(events, ct);
        return result;
    }

    // ------------------------------------------------------------------
    public async Task<SubmitAnswerResponse> SubmitAnswerAsync(
        Guid attemptId,
        SubmitAnswerRequest request,
        CancellationToken ct = default)
    {
        var events = new List<InterviewAttemptEvent>();
        var result = await WithAttemptLockAsync(attemptId, async () =>
        {
            var attempt = await LoadAttemptAsync(attemptId, ct);
            if (attempt.Participant.StudentId != currentUser.Id)
                throw new NotFoundException("attempt_not_found", "Attempt not found.");

            var session = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
            var allTurns = attempt.Responses.SelectMany(r => r.Turns).ToList();
            var submittedTurn = request.TurnId is Guid turnId
                ? allTurns.FirstOrDefault(t => t.Id == turnId)
                  ?? throw new ConflictException("turn_not_found", "The submitted turn does not belong to this attempt.")
                : attempt.CurrentResponse?.CurrentTurn;

            if (request.TurnId is not null && submittedTurn is { AnsweredAt: not null })
            {
                if (!string.Equals(NormalizeAnswer(request.AnswerText), submittedTurn.AnswerTranscript, StringComparison.Ordinal))
                {
                    var retryTime = UtcNow;
                    if (!session.IsOpenAt(retryTime) || retryTime > GetTurnDeadline(submittedTurn, session))
                        throw new ConflictException("answer_deadline_passed", "The answer deadline has passed.");
                    throw new ConflictException("turn_submission_conflict", "This turn was already submitted with a different answer.");
                }
                var previousResponse = attempt.Responses.Single(r => r.Turns.Any(t => t.Id == submittedTurn.Id));
                events.Add(CreateEvent(attempt, session.Id, "answer.duplicate", InterviewEventSequence.Current(attempt), UtcNow));
                return DuplicateResponse(attempt, previousResponse, submittedTurn, session);
            }

            attempt.EnsureInProgress();
            var response = attempt.CurrentResponse
                           ?? throw new ConflictException("no_open_question", "There is no question waiting for an answer.");
            var turn = response.CurrentTurn
                       ?? throw new ConflictException("no_open_turn", "There is no turn waiting for an answer.");
            if (submittedTurn is null || submittedTurn.Id != turn.Id)
                throw new ConflictException("turn_not_current", "The submitted turn is no longer current.");

            var now = UtcNow;
            if (!session.IsOpenAt(now) || now > GetTurnDeadline(turn, session))
                throw new ConflictException("answer_deadline_passed", "The answer deadline has passed.");

            return await ProcessTurnAsync(attempt, response, turn, session, NormalizeAnswer(request.AnswerText), now,
                timedOut: false, sessionOpen: true, events: events, ct: ct);
        }, ct);

        await PublishEventsAsync(events, ct);
        return result;
    }

    public async Task<int> ExpireOverdueAttemptsAsync(CancellationToken ct = default)
    {
        var attemptIds = await db.InterviewAttempts.AsNoTracking()
            .Where(a => a.Status == AttemptStatus.InProgress)
            .Select(a => a.Id)
            .ToListAsync(ct);

        var expiredCount = 0;
        foreach (var attemptId in attemptIds)
        {
            var attemptEvents = new List<InterviewAttemptEvent>();
            var expired = await WithAttemptLockAsync(attemptId, async () =>
            {
                var attempt = await LoadAttemptAsync(attemptId, ct);
                if (attempt.Status != AttemptStatus.InProgress)
                    return false;

                var response = attempt.CurrentResponse;
                var turn = response?.CurrentTurn;
                if (response is null || turn is null)
                    return false;

                var session = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
                var now = UtcNow;
                var sessionOpen = session.IsOpenAt(now);
                if (sessionOpen && now <= GetTurnDeadline(turn, session))
                    return false;

                await ProcessTurnAsync(attempt, response, turn, session, answer: null, now: now,
                    timedOut: true, sessionOpen: sessionOpen, events: attemptEvents, ct: ct);
                return true;
            }, ct);

            if (expired)
            {
                expiredCount++;
                await PublishEventsAsync(attemptEvents, ct);
            }
        }

        return expiredCount;
    }

    // ------------------------------------------------------------------
    public async Task<AttemptStateDto> GetAttemptStateAsync(Guid attemptId, CancellationToken ct = default)
    {
        var attempt = await LoadOwnAttemptAsync(attemptId, ct);
        var session = await examConfig.GetSessionAsync(attempt.Participant.ExamSessionId, ct)
                      ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");

        var response = attempt.CurrentResponse
                       ?? throw new ConflictException("no_open_question", "There is no question waiting for an answer.");
        var turn = response.CurrentTurn
                   ?? throw new ConflictException("no_open_turn", "The current question has already been answered.");
        if (request.TurnId == Guid.Empty)
            throw new ValidationException(new Dictionary<string, string[]> { ["turnId"] = ["turnId is required."] });
        if (request.TurnId != turn.Id)
            throw new ConflictException("turn_not_current", "The submitted answer does not belong to the current turn.");
        var question = session.Questions.First(q => q.Id == response.QuestionId);

        // 1) Store the answer. Past the time limit + grace => timed out, the transcript is not stored.
        var now = UtcNow;
        var deadline = turn.AskedAt.AddSeconds(session.TimeLimitPerAnswerSeconds + _options.TimeLimitGraceSeconds);
        var timedOut = now > deadline;
        var answer = string.IsNullOrWhiteSpace(request.AnswerText) ? null : request.AnswerText.Trim();

        if (!timedOut && answer is not null)
        {
            turn.AnswerTranscript = answer;
            turn.AnsweredAt = now;
        }
        // timed out / empty: keep AnswerTranscript = null, AnsweredAt = null (same as the seed data)

        // Persist the received answer before waiting on an external provider. If
        // evaluation fails, the catch below reopens the same turn while keeping
        // this transcript for a safe retry.
        await db.SaveChangesAsync(ct);
        response.AiEvaluationState = AiEvaluationState.Pending;
        response.AiEvaluationError = null;
        await db.SaveChangesAsync(ct);

        var dialogue = response.Turns.OrderBy(t => t.TurnNo)
            .Select(t => new DialogueTurn(t.QuestionText, t.AnswerTranscript, t.Type == TurnType.FollowUp))
            .ToList();
        try
        {
            // 2) Analyze -> follow-up?
            var analysis = await analyzer.AnalyzeAsync(new AnswerAnalysisRequest(
                question.Content, question.Criteria, dialogue,
                response.FollowUpCount, session.MaxFollowUps, _options.Language), ct);

            if (analysis.NeedsFollowUp && response.FollowUpCount < session.MaxFollowUps
                                       && !string.IsNullOrWhiteSpace(analysis.FollowUpQuestion))
            {
                response.AddTurn(TurnType.FollowUp, analysis.FollowUpQuestion!, now);
                response.AiEvaluationState = AiEvaluationState.Completed;
                response.AiEvaluationError = null;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Attempt {AttemptId}: follow-up {Count} for question {QuestionId}",
                    attempt.Id, response.FollowUpCount, question.Id);
                return new SubmitAnswerResponse(SubmitOutcome.FollowUp, timedOut, null, ToState(attempt, session));
            }

            // 3) Main question done -> score against the rubric (stored once, Reporting only reads it)
            var score = await scorer.ScoreAsync(new RubricScoringRequest(
                question.Content, question.Criteria, dialogue, _options.Language), ct);

            // One CriterionScore per rubric criterion, clamped to 0..MaxPoints (a missing criterion counts as 0)
            var criterionScores = question.Criteria.Select(c =>
            {
                var given = score.Scores.FirstOrDefault(s => s.CriterionId == c.Id);
                return new CriterionScore
                {
                    Id = Guid.NewGuid(),
                    QuestionResponseId = response.Id,
                    RubricCriterionId = c.Id,
                    Points = Math.Clamp(given?.Points ?? 0, 0, c.MaxPoints),
                    Comment = given?.Comment,
                };
            }).ToList();
            response.RecordAiEvaluation(criterionScores, score.Feedback, now);
            db.CriterionScores.AddRange(criterionScores);
            var suggested = response.AiSuggestedScore!.Value;

            // 4) Next question or finish
            var next = session.Questions.FirstOrDefault(q => q.OrderNo > question.OrderNo);
            string outcome;
            if (next is not null)
            {
                AskMainQuestion(attempt, next, now);
                outcome = SubmitOutcome.NextQuestion;
            }
            else
            {
                attempt.Complete(now);
                attempt.Participant.Status = ParticipantStatus.Completed;
                outcome = SubmitOutcome.Completed;
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Attempt {AttemptId}: question {QuestionId} scored {Score}/{Max}, outcome {Outcome}",
                attempt.Id, question.Id, suggested, question.MaxScore, outcome);

            var evaluation = _options.ShowAiScoreToStudent
                ? new EvaluationDto(question.Id, suggested, question.MaxScore, score.Feedback)
                : null;
            return new SubmitAnswerResponse(outcome, timedOut, evaluation, ToState(attempt, session));
        }
        catch (AiEvaluationException)
        {
            // Keep the transcript/audio reference, but reopen the turn so a
            // retry can run the provider again instead of silently scoring zero.
            turn.AnsweredAt = null;
            response.AiEvaluationState = AiEvaluationState.Failed;
            response.AiEvaluationError = "AI evaluation failed. Retry is available.";
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Attempt {AttemptId}: AI evaluation failed for question {QuestionId}; answer kept for retry",
                attempt.Id, question.Id);
            throw;
        }
    }

    private string? NormalizeAnswer(string? answer) =>
        string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();

    private static InterviewAttemptEvent CreateEvent(
        InterviewAttempt attempt, Guid sessionId, string type, long sequence, DateTime occurredAt) =>
        new(Guid.NewGuid(), attempt.Id, sessionId, sequence, type, occurredAt);

    private async Task PublishEventsAsync(IEnumerable<InterviewAttemptEvent> events, CancellationToken ct)
    {
        foreach (var interviewEvent in events)
            await eventPublisher.PublishAsync(interviewEvent, ct);
    }

    private DateTime GetTurnDeadline(InterviewTurn turn, ExamSessionInfo session)
    {
        var deadline = turn.AskedAt.AddSeconds(session.TimeLimitPerAnswerSeconds + Math.Max(0, _options.TimeLimitGraceSeconds));
        return session.EndTime < deadline ? session.EndTime : deadline;
    }

    private async Task<T> WithAttemptLockAsync<T>(Guid attemptId, Func<Task<T>> operation, CancellationToken ct)
    {
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM interview_attempts WHERE id = {attemptId} FOR UPDATE", ct);
            var result = await operation();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }

        var inMemoryResult = await operation();
        await db.SaveChangesAsync(ct);
        return inMemoryResult;
    }

    private async Task<T> WithParticipantLockAsync<T>(Guid participantId, Func<Task<T>> operation, CancellationToken ct)
    {
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM participants WHERE id = {participantId} FOR UPDATE", ct);
            var result = await operation();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }

        var inMemoryResult = await operation();
        await db.SaveChangesAsync(ct);
        return inMemoryResult;
    }

    private void AskMainQuestion(InterviewAttempt attempt, QuestionInfo question, DateTime now)
    {
        var response = new QuestionResponse
        {
            Id = Guid.NewGuid(),
            AttemptId = attempt.Id,
            QuestionId = question.Id,
        };
        response.AddTurn(TurnType.Main, question.Content, now);
        attempt.Responses.Add(response);
        // Explicit Add: stops EF from treating the new entity (Guid already set) as an existing row to UPDATE
        db.QuestionResponses.Add(response);
    }

    private AttemptStateDto ToState(InterviewAttempt attempt, ExamSessionInfo session)
    {
        CurrentTurnDto? current = null;
        var response = attempt.Status == AttemptStatus.InProgress ? attempt.CurrentResponse : null;
        var turn = response?.CurrentTurn;
        if (response is not null && turn is not null)
        {
            var q = session.Questions.First(x => x.Id == response.QuestionId);
            current = new CurrentTurnDto(
                response.Id, turn.Id, q.Id, q.OrderNo, turn.TurnNo, turn.Type.ToString(), turn.QuestionText,
                turn.AskedAt, turn.AskedAt.AddSeconds(session.TimeLimitPerAnswerSeconds));
        }

        return new AttemptStateDto(
            attempt.Id, session.Id, session.Title, attempt.Status.ToString(),
            session.TimeLimitPerAnswerSeconds, session.MaxFollowUps, session.Questions.Count,
            attempt.Responses.Count(r => r.IsFinished),
            attempt.StartedAt, attempt.CompletedAt, current);
    }
}
