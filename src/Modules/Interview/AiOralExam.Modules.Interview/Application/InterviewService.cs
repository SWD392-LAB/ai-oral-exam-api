using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview.Application;

public interface IInterviewService
{
    Task<IReadOnlyList<MySessionDto>> GetMySessionsAsync(CancellationToken ct = default);

    /// <summary>POST /api/interview-attempts (BE-PLAT-04). Returns (state, created).</summary>
    Task<(AttemptStateDto State, bool Created)> StartAttemptAsync(StartAttemptRequest request, CancellationToken ct = default);

    /// <summary>POST /api/interview-attempts/{id}/responses (BE-AI-05).</summary>
    Task<SubmitAnswerResponse> SubmitAnswerAsync(Guid attemptId, SubmitAnswerRequest request, CancellationToken ct = default);

    Task<AttemptStateDto> GetAttemptStateAsync(Guid attemptId, CancellationToken ct = default);

    Task<AttemptResult> GetAttemptReviewAsync(Guid attemptId, CancellationToken ct = default);

    Task<ScoreReviewResult> SubmitScoreReviewAsync(Guid attemptId, SubmitScoreReviewRequest request, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates the exam flow:
///   Start  -> create InterviewAttempt + QuestionResponse for question 1 + a Main turn
///   Submit -> store the transcript -> analyze (IAnswerAnalyzer) -> follow-up OR score (IRubricScorer)
///          -> next question OR finish the attempt (PendingReview)
/// All changes of one Submit are saved in one transaction (a single SaveChanges).
/// </summary>
internal sealed class InterviewService(
    InterviewDbContext db,
    IExamConfigurationApi examConfig,
    IInterviewResultsApi results,
    IAnswerAnalyzer analyzer,
    IRubricScorer scorer,
    ICurrentUser currentUser,
    TimeProvider clock,
    IOptions<InterviewOptions> options,
    ILogger<InterviewService> logger) : IInterviewService
{
    private const string DefaultLanguage = "Vietnamese"; // TODO (M2): read from AiServiceConfig through the F7 contract
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

        var participant = await db.Participants
                              .Include(p => p.Attempts)
                              .FirstOrDefaultAsync(p => p.ExamSessionId == session.Id && p.StudentId == me, ct)
                          ?? throw new ForbiddenException("You are not registered for this exam session.");

        // Attempt already running (e.g. the page was refreshed) -> return it instead of creating a new one
        var running = participant.Attempts.FirstOrDefault(a => a.Status == AttemptStatus.InProgress);
        if (running is not null)
            return (await GetAttemptStateAsync(running.Id, ct), false);

        var now = UtcNow;
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

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Attempt {AttemptId} started by {StudentId} for session {SessionId}",
            attempt.Id, me, session.Id);

        return (ToState(attempt, session), true);
    }

    // ------------------------------------------------------------------
    public async Task<SubmitAnswerResponse> SubmitAnswerAsync(Guid attemptId, SubmitAnswerRequest request, CancellationToken ct = default)
    {
        var attempt = await LoadOwnAttemptAsync(attemptId, ct);
        attempt.EnsureInProgress();

        var session = await examConfig.GetSessionAsync(attempt.Participant.ExamSessionId, ct)
                      ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");

        var response = attempt.CurrentResponse
                       ?? throw new ConflictException("no_open_question", "There is no question waiting for an answer.");
        var turn = response.CurrentTurn
                   ?? throw new ConflictException("no_open_turn", "The current question has already been answered.");
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

        var dialogue = response.Turns.OrderBy(t => t.TurnNo)
            .Select(t => new DialogueTurn(t.QuestionText, t.AnswerTranscript, t.Type == TurnType.FollowUp))
            .ToList();
        // 2) Analyze -> follow-up?
        var analysis = await analyzer.AnalyzeAsync(new AnswerAnalysisRequest(
            question.Content, question.Criteria, dialogue,
            response.FollowUpCount, session.MaxFollowUps, DefaultLanguage), ct);

        if (analysis.NeedsFollowUp && response.FollowUpCount < session.MaxFollowUps
                                   && !string.IsNullOrWhiteSpace(analysis.FollowUpQuestion))
        {
            response.AddTurn(TurnType.FollowUp, analysis.FollowUpQuestion!, now);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Attempt {AttemptId}: follow-up {Count} for question {QuestionId}",
                attempt.Id, response.FollowUpCount, question.Id);
            return new SubmitAnswerResponse(SubmitOutcome.FollowUp, timedOut, null, ToState(attempt, session));
        }

        // 3) Main question done -> score against the rubric (stored once, Reporting only reads it)
        var score = await scorer.ScoreAsync(new RubricScoringRequest(
            question.Content, question.Criteria, dialogue, DefaultLanguage), ct);

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

    // ------------------------------------------------------------------
    public async Task<AttemptStateDto> GetAttemptStateAsync(Guid attemptId, CancellationToken ct = default)
    {
        var attempt = await LoadOwnAttemptAsync(attemptId, ct);
        var session = await examConfig.GetSessionAsync(attempt.Participant.ExamSessionId, ct)
                      ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        return ToState(attempt, session);
    }

    public async Task<AttemptResult> GetAttemptReviewAsync(Guid attemptId, CancellationToken ct = default)
    {
        var attempt = await LoadReviewAttemptAsync(attemptId, ct);
        if (attempt.Status == AttemptStatus.InProgress)
            throw new ConflictException("attempt_not_ready_for_review", "The attempt has not been completed.");
        if (attempt.Review is { Status: ReviewStatus.Pending } review && review.ReviewerId != currentUser.Id)
            throw new ForbiddenException("This review is assigned to another lecturer.");

        return await results.GetAttemptResultAsync(attemptId, ct)
               ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");
    }

    public async Task<ScoreReviewResult> SubmitScoreReviewAsync(
        Guid attemptId,
        SubmitScoreReviewRequest request,
        CancellationToken ct = default)
    {
        var attempt = await LoadReviewAttemptAsync(attemptId, ct);
        if (attempt.Status != AttemptStatus.PendingReview)
            throw new ConflictException("attempt_not_pending_review", "Only attempts pending review can be finalized.");
        if (attempt.Review is { Status: not ReviewStatus.Pending })
            throw new ConflictException("score_review_already_submitted", "A score review has already been submitted.");
        if (attempt.Review is { } assignedReview && assignedReview.ReviewerId != currentUser.Id)
            throw new ForbiddenException("This review is assigned to another lecturer.");

        var session = await examConfig.GetSessionAsync(attempt.Participant.ExamSessionId, ct)
                      ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        ValidateScoreReviewRequest(request, session.TotalMaxScore);

        var now = UtcNow;
        var review = attempt.Review ?? new ScoreReview
        {
            Id = Guid.NewGuid(),
            AttemptId = attempt.Id,
            ReviewerId = currentUser.Id
        };
        review.FinalScore = request.FinalScore;
        review.Comment = request.Comment;
        review.Status = ReviewStatus.Approved;
        review.ReviewedAt = now;
        if (attempt.Review is null)
        {
            attempt.Review = review;
            db.ScoreReviews.Add(review);
        }

        attempt.TransitionTo(AttemptStatus.Finalized);
        await db.SaveChangesAsync(ct);

        return new(review.ReviewerId, review.FinalScore, review.Comment, review.Status, review.ReviewedAt);
    }

    private async Task<InterviewAttempt> LoadReviewAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        if (!currentUser.IsInRole(Roles.Lecturer))
            throw new ForbiddenException("Only lecturers can review attempts.");

        var attempt = await db.InterviewAttempts
                          .Include(a => a.Participant)
                          .Include(a => a.Review)
                          .FirstOrDefaultAsync(a => a.Id == attemptId, ct)
                      ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");

        if (!await examConfig.IsLecturerOfSessionAsync(currentUser.Id, attempt.Participant.ExamSessionId, ct))
            throw new NotFoundException("attempt_not_found", "Attempt not found.");

        return attempt;
    }

    private static void ValidateScoreReviewRequest(SubmitScoreReviewRequest request, decimal sessionMaxScore)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.FinalScore < 0 || request.FinalScore > 999.99m || request.FinalScore > sessionMaxScore)
            errors[nameof(request.FinalScore)] = ["Final score must be non-negative and no greater than the session maximum."];
        else if (request.FinalScore != decimal.Round(request.FinalScore, 2))
            errors[nameof(request.FinalScore)] = ["Final score must have no more than two decimal places."];

        if (errors.Count > 0)
            throw new ValidationException(errors);
    }

    // ------------------------------------------------------------------
    private async Task<InterviewAttempt> LoadOwnAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        var attempt = await db.InterviewAttempts
                          .Include(a => a.Participant)
                          .Include(a => a.Responses).ThenInclude(r => r.Turns)
                          .AsSplitQuery()
                          .FirstOrDefaultAsync(a => a.Id == attemptId, ct)
                      ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");

        // Students only see their own attempts
        if (attempt.Participant.StudentId != currentUser.Id)
            throw new NotFoundException("attempt_not_found", "Attempt not found.");
        return attempt;
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

    private static AttemptStateDto ToState(InterviewAttempt attempt, ExamSessionInfo session)
    {
        CurrentTurnDto? current = null;
        var response = attempt.Status == AttemptStatus.InProgress ? attempt.CurrentResponse : null;
        var turn = response?.CurrentTurn;
        if (response is not null && turn is not null)
        {
            var q = session.Questions.First(x => x.Id == response.QuestionId);
            current = new CurrentTurnDto(
                response.Id, q.Id, q.OrderNo, turn.TurnNo, turn.Type.ToString(), turn.QuestionText,
                turn.AskedAt, turn.AskedAt.AddSeconds(session.TimeLimitPerAnswerSeconds));
        }

        return new AttemptStateDto(
            attempt.Id, session.Id, session.Title, attempt.Status.ToString(),
            session.TimeLimitPerAnswerSeconds, session.MaxFollowUps, session.Questions.Count,
            attempt.Responses.Count(r => r.IsFinished),
            attempt.StartedAt, attempt.CompletedAt, current);
    }
}
