using System.Text.Json;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Application.Audio;
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
/// Answer acceptance/checkpoints and AI results are committed in separate short transactions.
/// </summary>
internal sealed class InterviewService(
    InterviewDbContext db,
    IExamConfigurationApi examConfig,
    IAnswerAnalyzer analyzer,
    IRubricScorer scorer,
    IAudioChunkStore audioStore,
    ISpeechToTextService speechToText,
    IInterviewEventPublisher eventPublisher,
    IAuditLogWriter auditLogWriter,
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

            if (!_options.AllowRetake && participant.Attempts.Any(a =>
                    a.Status is AttemptStatus.PendingReview or AttemptStatus.Finalized))
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
            participant.ReconcileStatus();
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
        if (request.TurnId == Guid.Empty)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(request.TurnId)] = ["turnId is required and must be a non-empty GUID."]
            });

        var claim = await WithAttemptLockAsync(attemptId, async () =>
        {
            var attempt = await LoadAttemptAsync(attemptId, ct);
            if (attempt.Participant.StudentId != currentUser.Id)
                throw new NotFoundException("attempt_not_found", "Attempt not found.");

            var session = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
            var allTurns = attempt.Responses.SelectMany(r => r.Turns).ToList();
            var submittedTurn = allTurns.FirstOrDefault(t => t.Id == request.TurnId)
                ?? throw new ConflictException("turn_not_found", "The submitted turn does not belong to this attempt.");

            if (submittedTurn.ProcessingStatus == TurnProcessingStatus.Failed)
                throw new ConflictException("turn_processing_failed", "Turn processing has permanently failed.");

            if (submittedTurn.AnsweredAt is not null || submittedTurn.IsTimedOut)
            {
                if (submittedTurn.IsTimedOut)
                    throw new ConflictException("answer_deadline_passed", "The turn expired without a submitted answer.");
                if (!string.Equals(NormalizeAnswer(request.AnswerText), submittedTurn.AnswerTranscript, StringComparison.Ordinal))
                {
                    throw new ConflictException("turn_submission_conflict", "This turn was already submitted with a different answer.");
                }
                var previousResponse = attempt.Responses.Single(r => r.Turns.Any(t => t.Id == submittedTurn.Id));
                if (submittedTurn.ProcessingStatus == TurnProcessingStatus.AwaitingAnswer)
                    submittedTurn.ProcessingStatus = TurnProcessingStatus.Completed;
                if (submittedTurn.ProcessingStatus == TurnProcessingStatus.Processing)
                    return new TurnSubmissionClaim(null, DuplicateResponse(attempt, previousResponse, submittedTurn, session),
                        CreateEvent(attempt, session.Id, "answer.duplicate", InterviewEventSequence.Current(attempt), UtcNow,
                            submittedTurn.Id));
                if (submittedTurn.ProcessingStatus == TurnProcessingStatus.Completed)
                    return new TurnSubmissionClaim(null, GetOriginalResponse(attempt, previousResponse, submittedTurn, session),
                        CreateEvent(attempt, session.Id, "answer.duplicate", InterviewEventSequence.Current(attempt), UtcNow,
                            submittedTurn.Id));

                var retryNow = UtcNow;
                if (submittedTurn.ProcessingStatus == TurnProcessingStatus.Retry
                    && submittedTurn.NextRetryAt is DateTime nextRetryAt && nextRetryAt > retryNow)
                    return new TurnSubmissionClaim(null, DuplicateResponse(attempt, previousResponse, submittedTurn, session),
                        CreateEvent(attempt, session.Id, "answer.duplicate", InterviewEventSequence.Current(attempt), retryNow,
                            submittedTurn.Id));

                submittedTurn.ProcessingStatus = TurnProcessingStatus.Processing;
                submittedTurn.ProcessingStartedAt = retryNow;
                submittedTurn.NextRetryAt = null;
                submittedTurn.ProcessingToken = Guid.NewGuid();
                return new TurnSubmissionClaim(new TurnProcessingClaim(attempt, previousResponse, submittedTurn,
                    session, NormalizeAnswer(request.AnswerText), retryNow, TimedOut: false, SessionOpen: session.IsOpenAt(retryNow),
                    ProcessingToken: submittedTurn.ProcessingToken.Value), null);
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

            turn.AnswerTranscript = NormalizeAnswer(request.AnswerText);
            turn.AnsweredAt = now;
            turn.ProcessingStatus = TurnProcessingStatus.Processing;
            turn.ProcessingStartedAt = now;
            turn.ProcessingToken = Guid.NewGuid();
            return new TurnSubmissionClaim(new TurnProcessingClaim(attempt, response, turn, session,
                turn.AnswerTranscript, now, TimedOut: false, SessionOpen: true,
                ProcessingToken: turn.ProcessingToken.Value), null, null);
        }, ct);

        if (claim.Response is not null)
        {
            if (claim.Event is not null)
                await eventPublisher.PublishAsync(claim.Event, ct);
            return claim.Response;
        }

        return await ProcessClaimedTurnAsync(attemptId, claim.Processing!, ct);
    }

    public async Task<SubmitAnswerResponse> SubmitAudioAnswerAsync(
        Guid attemptId,
        Guid turnId,
        DateTime receiptAt,
        CancellationToken ct = default)
    {
        if (turnId == Guid.Empty)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(turnId)] = ["turnId is required and must be a non-empty GUID."]
            });
        EnsureAudioStudent();

        var claim = await WithAttemptLockAsync(attemptId, async () =>
        {
            var attempt = await LoadAttemptAsync(attemptId, ct);
            EnsureAudioStudent();
            if (attempt.Participant.StudentId != currentUser.Id)
                throw new NotFoundException("attempt_not_found", "Attempt not found.");

            var session = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
            var turn = attempt.Responses.SelectMany(r => r.Turns)
                .FirstOrDefault(t => t.Id == turnId)
                ?? throw new ConflictException("turn_not_found", "The submitted turn does not belong to this attempt.");
            var response = attempt.Responses.Single(r => r.Turns.Any(t => t.Id == turnId));

            if (turn.ProcessingStatus == TurnProcessingStatus.Completed)
                return new AudioSubmissionClaim(null, GetOriginalResponse(attempt, response, turn, session));
            if (turn.ProcessingStatus == TurnProcessingStatus.Failed)
                throw new ConflictException("turn_processing_failed", "Turn processing has permanently failed.");
            if (turn.IsTimedOut || turn.AnsweredAt is not null && turn.AudioAcceptedAt is null)
                throw new ConflictException("turn_submission_conflict", "This turn was already submitted.");
            if (turn.ProcessingStatus == TurnProcessingStatus.Processing
                || turn.ProcessingStatus == TurnProcessingStatus.Retry
                    && turn.NextRetryAt is DateTime retryAt && retryAt > UtcNow)
                return new AudioSubmissionClaim(null, DuplicateResponse(attempt, response, turn, session));

            attempt.EnsureInProgress();
            if (turn.AudioAcceptedAt is null)
            {
                if (turn.AudioCompletionReceivedAt is null)
                {
                    if (attempt.CurrentResponse?.Id != response.Id
                        || response.CurrentTurn?.Id != turn.Id)
                        throw new ConflictException(
                            "turn_not_current",
                            "The submitted turn is no longer current.");

                    if (!session.IsOpenAt(receiptAt)
                        || receiptAt > GetTurnDeadline(turn, session))
                        throw new ConflictException(
                            "answer_deadline_passed",
                            "The answer deadline has passed.");

                    turn.AudioCompletionReceivedAt = receiptAt;
                }
            }
            else if (turn.ProcessingStatus != TurnProcessingStatus.Retry)
                throw new ConflictException("turn_processing_failed", "The accepted audio turn cannot be reclaimed.");

            var now = UtcNow;
            turn.ProcessingStatus = TurnProcessingStatus.Processing;
            turn.ProcessingStartedAt = now;
            turn.NextRetryAt = null;
            turn.ProcessingToken = Guid.NewGuid();
            return new AudioSubmissionClaim(new TurnProcessingClaim(attempt, response, turn, session,
                turn.AnswerTranscript, now, turn.IsTimedOut, session.IsOpenAt(now),
                turn.ProcessingToken.Value, turn.ProcessingStage), null);
        }, ct);

        if (claim.Response is not null)
            return claim.Response;
        return await ProcessClaimedTurnAsync(attemptId, claim.Processing!, ct);
    }

    public async Task<SubmitAnswerResponse> RecoverInterruptedTurnAsync(
        Guid attemptId, Guid turnId, CancellationToken ct = default)
    {
        EnsureRecoveryActor();

        var claim = await WithAttemptLockAsync(attemptId, async () =>
        {
            var attempt = await LoadAttemptAsync(attemptId, ct);
            var session = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
            await EnsureRecoveryActorAsync(session.Id, ct);

            if (attempt.Status != AttemptStatus.Interrupted)
                throw new ConflictException("attempt_not_interrupted", "Only interrupted attempts can be recovered.");

            var response = attempt.Responses.SingleOrDefault(r => r.Turns.Any(t => t.Id == turnId))
                ?? throw new ConflictException("turn_not_found", "The selected turn does not belong to this attempt.");
            var turn = response.Turns.Single(t => t.Id == turnId);
            var stage = turn.ProcessingStage;
            var recoveryAudit = new RecoveryAuditData(
                attempt.Id,
                turn.Id,
                attempt.Status.ToString(),
                turn.ProcessingStatus.ToString(),
                turn.RetryCount,
                turn.TranscriptionRetryCount,
                stage.ToString());

            if (stage == TurnProcessingStage.AwaitingAnswer || stage == TurnProcessingStage.TimeoutEvaluation)
                throw new ConflictException("turn_recovery_stage_invalid", "The failed turn has no recoverable processing stage.");
            if (stage == TurnProcessingStage.Transcription && turn.AudioAcceptedAt is null)
                throw new ConflictException("audio_answer_not_accepted", "Transcription recovery requires accepted audio.");
            if (stage == TurnProcessingStage.AiEvaluation
                && (turn.AnsweredAt is null || turn.AnswerTranscript is null))
                throw new ConflictException("transcript_not_persisted", "AI recovery requires a persisted transcript.");
            if (stage == TurnProcessingStage.AudioValidation && turn.AudioCompletionReceivedAt is null)
                throw new ConflictException("audio_answer_not_received", "Audio recovery requires a completed audio receipt.");

            turn.PrepareManualRecovery(attempt.Status);
            turn.ProcessingStatus = TurnProcessingStatus.Processing;
            turn.ProcessingStartedAt = UtcNow;
            turn.ProcessingToken = Guid.NewGuid();
            var token = turn.ProcessingToken.Value;
            return new TurnProcessingClaim(attempt, response, turn, session,
                turn.AnswerTranscript, turn.ProcessingStartedAt.Value, turn.IsTimedOut,
                session.IsOpenAt(turn.ProcessingStartedAt.Value), token, stage, ManualRecovery: true,
                RecoveryAudit: recoveryAudit);
        }, ct);

        SubmitAnswerResponse result;
        try
        {
            result = await ProcessClaimedTurnAsync(attemptId, claim, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                var failed = await LoadAttemptAsync(attemptId, CancellationToken.None);
                var failedTurn = failed.Responses.SelectMany(r => r.Turns).Single(t => t.Id == turnId);
                await WriteRecoveryAuditAsync(claim.RecoveryAudit!, AuditLogActions.ManualRecoveryFailed,
                    failedTurn.ProcessingStatus.ToString(), CancellationToken.None);
            }
            catch (Exception auditException)
            {
                logger.LogError(auditException, "Failed to audit manual recovery failure for attempt {AttemptId}, turn {TurnId}.",
                    attemptId, turnId);
            }
            throw;
        }

        var completed = await LoadAttemptAsync(attemptId, CancellationToken.None);
        var completedTurn = completed.Responses.SelectMany(r => r.Turns).Single(t => t.Id == turnId);
        if (completed.Status == AttemptStatus.PendingReview
            && completedTurn.ProcessingStatus == TurnProcessingStatus.Completed)
        {
            try
            {
                await WriteRecoveryAuditAsync(claim.RecoveryAudit!, AuditLogActions.ManualRecoverySucceeded,
                    "Completed", CancellationToken.None);
            }
            catch (Exception auditException)
            {
                logger.LogError(auditException,
                    "Failed to audit successful manual recovery for attempt {AttemptId}, turn {TurnId}, actor {ActorId}, action {AuditAction}.",
                    attemptId, turnId, currentUser.Id, AuditLogActions.ManualRecoverySucceeded);
                throw;
            }
        }
        else
        {
            await WriteRecoveryAuditAsync(claim.RecoveryAudit!, AuditLogActions.ManualRecoveryFailed,
                completedTurn.ProcessingStatus.ToString(), CancellationToken.None);
        }
        return result;
    }

    public async Task<int> ExpireOverdueAttemptsAsync(CancellationToken ct = default)
    {
        var attemptIds = await db.InterviewAttempts.AsNoTracking()
            .Where(a => a.Status == AttemptStatus.InProgress
                || a.Status == AttemptStatus.Interrupted
                    && a.Responses.SelectMany(r => r.Turns).Any(t => t.ManualRecoveryCount > 0
                        && (t.ProcessingStatus == TurnProcessingStatus.Retry
                            || t.ProcessingStatus == TurnProcessingStatus.Processing)))
            .Select(a => a.Id)
            .ToListAsync(ct);

        var expiredCount = 0;
        foreach (var attemptId in attemptIds)
        {
            try
            {
                if (await ProcessOverdueAttemptAsync(attemptId, ct))
                    expiredCount++;
            }
            catch (OperationCanceledException exception)
                when (ct.IsCancellationRequested && exception.CancellationToken == ct)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Failed to process overdue interview attempt {AttemptId}; continuing with remaining attempts.",
                    attemptId);
            }
        }

        return expiredCount;
    }

    private async Task<bool> ProcessOverdueAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        var claim = await WithAttemptLockAsync(attemptId, async () =>
        {
            var attempt = await LoadAttemptAsync(attemptId, ct);
            var manualRecoveryAttempt = attempt.Status == AttemptStatus.Interrupted
                && attempt.Responses.SelectMany(r => r.Turns).Any(t => t.ManualRecoveryCount > 0);
            if (attempt.Status != AttemptStatus.InProgress && !manualRecoveryAttempt)
                return null;

            var now = UtcNow;
            if (attempt.Status == AttemptStatus.InProgress
                && attempt.Responses.SelectMany(r => r.Turns)
                    .Any(t => t.ProcessingStatus == TurnProcessingStatus.Failed))
            {
                attempt.Interrupt(now);
                attempt.Participant.ReconcileStatus();
                return null;
            }

            var staleProcessingCutoff = now.AddMinutes(-2);
            var pendingTurn = attempt.Responses.SelectMany(r => r.Turns)
                .Where(t => t.ProcessingStatus == TurnProcessingStatus.Retry
                        && (t.NextRetryAt == null || t.NextRetryAt <= now)
                    || t.ProcessingStatus == TurnProcessingStatus.Processing
                        && t.ProcessingStartedAt < staleProcessingCutoff)
                .Where(t => attempt.Status == AttemptStatus.InProgress || t.ManualRecoveryCount > 0)
                .OrderBy(t => t.NextRetryAt ?? t.ProcessingStartedAt)
                .FirstOrDefault();
            if (pendingTurn is not null)
            {
                if (pendingTurn.ProcessingStatus == TurnProcessingStatus.Processing)
                {
                    if (pendingTurn.AudioAcceptedAt is null
                        && pendingTurn.AudioCompletionReceivedAt is null
                        && pendingTurn.AnsweredAt is null
                        && !pendingTurn.IsTimedOut)
                    {
                        pendingTurn.ProcessingStatus = TurnProcessingStatus.AwaitingAnswer;
                        pendingTurn.ProcessingStartedAt = null;
                        pendingTurn.ProcessingToken = null;
                        pendingTurn.NextRetryAt = null;
                        return null;
                    }

                    var stage = pendingTurn.ProcessingStage;
                    var retryCount = stage == TurnProcessingStage.Transcription
                        ? ++pendingTurn.TranscriptionRetryCount
                        : ++pendingTurn.RetryCount;
                    pendingTurn.ProcessingStartedAt = null;
                    pendingTurn.ProcessingToken = null;
                    if (retryCount > Math.Max(0, _options.MaximumTurnRetries))
                    {
                        pendingTurn.ProcessingStatus = TurnProcessingStatus.Failed;
                        pendingTurn.NextRetryAt = null;
                        if (attempt.Status == AttemptStatus.InProgress)
                        {
                            attempt.Interrupt(now);
                            attempt.Participant.ReconcileStatus();
                        }
                        return null;
                    }

                    pendingTurn.ProcessingStatus = TurnProcessingStatus.Retry;
                    pendingTurn.NextRetryAt = now.Add(CalculateRetryDelay(retryCount, _options));
                    return null;
                }

                pendingTurn.ProcessingStatus = TurnProcessingStatus.Processing;
                pendingTurn.ProcessingStartedAt = now;
                pendingTurn.NextRetryAt = null;
                pendingTurn.ProcessingToken = Guid.NewGuid();
                var pendingResponse = attempt.Responses.Single(r => r.Id == pendingTurn.QuestionResponseId);
                var pendingSession = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
                return new TurnProcessingClaim(attempt, pendingResponse, pendingTurn, pendingSession,
                    pendingTurn.AnswerTranscript, now, pendingTurn.IsTimedOut, pendingSession.IsOpenAt(now),
                    pendingTurn.ProcessingToken.Value, pendingTurn.ProcessingStage,
                    ManualRecovery: attempt.Status == AttemptStatus.Interrupted);
            }

            var response = attempt.CurrentResponse;
            var turn = response?.CurrentTurn;
            if (response is null || turn is null || turn.ProcessingStatus != TurnProcessingStatus.AwaitingAnswer)
                return null;

            var session = await GetSessionAsync(attempt.Participant.ExamSessionId, ct);
            var sessionOpen = session.IsOpenAt(now);
            if (sessionOpen && now <= GetTurnDeadline(turn, session))
                return null;

            turn.IsTimedOut = true;
            turn.ProcessingStatus = TurnProcessingStatus.Processing;
            turn.ProcessingStartedAt = now;
            turn.ProcessingToken = Guid.NewGuid();
            return new TurnProcessingClaim(attempt, response, turn, session, Answer: null, now,
                TimedOut: true, SessionOpen: sessionOpen, ProcessingToken: turn.ProcessingToken.Value,
                Stage: TurnProcessingStage.TimeoutEvaluation);
        }, ct);

        if (claim is null)
            return false;

        await ProcessClaimedTurnAsync(attemptId, claim, ct);
        return true;
    }

    // ------------------------------------------------------------------
    public async Task<AttemptStateDto> GetAttemptStateAsync(Guid attemptId, CancellationToken ct = default)
    {
        var attempt = await LoadOwnAttemptAsync(attemptId, ct);
        var session = await examConfig.GetSessionAsync(attempt.Participant.ExamSessionId, ct)
                      ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        var currentTurn = attempt.CurrentResponse?.CurrentTurn;
        var now = UtcNow;
        var processingNeedsRecovery = attempt.Responses.SelectMany(r => r.Turns).Any(t =>
            t.ProcessingStatus == TurnProcessingStatus.Retry && (t.NextRetryAt == null || t.NextRetryAt <= now)
            || t.ProcessingStatus == TurnProcessingStatus.Processing && t.ProcessingStartedAt < now.AddMinutes(-2)
            || t.ProcessingStatus == TurnProcessingStatus.Failed);
        if (processingNeedsRecovery || currentTurn is not null
            && (!session.IsOpenAt(now) || now > GetTurnDeadline(currentTurn, session)))
        {
            await ProcessOverdueAttemptAsync(attemptId, ct);
            attempt = await LoadOwnAttemptAsync(attemptId, ct);
        }
        return ToState(attempt, session);
    }

    public async Task<Guid> AuthorizeAttemptSubscriptionAsync(Guid attemptId, CancellationToken ct = default)
    {
        var attempt = await db.InterviewAttempts.AsNoTracking()
            .Where(a => a.Id == attemptId)
            .Select(a => new { a.Participant.ExamSessionId, a.Participant.StudentId })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");

        if (currentUser.IsInRole(Roles.Student))
        {
            if (attempt.StudentId != currentUser.Id)
                throw new NotFoundException("attempt_not_found", "Attempt not found.");
            return attempt.ExamSessionId;
        }

        if (currentUser.IsInRole(Roles.Lecturer)
            && await examConfig.IsLecturerOfSessionAsync(currentUser.Id, attempt.ExamSessionId, ct))
            return attempt.ExamSessionId;

        throw new ForbiddenException("You are not authorized to subscribe to this interview attempt.");
    }

    public async Task AuthorizeSessionSubscriptionAsync(Guid examSessionId, CancellationToken ct = default)
    {
        if (!currentUser.IsInRole(Roles.Lecturer)
            || !await examConfig.IsLecturerOfSessionAsync(currentUser.Id, examSessionId, ct))
            throw new NotFoundException("exam_session_not_found", "Exam session not found.");
    }

    // ------------------------------------------------------------------
    private async Task<InterviewAttempt> LoadOwnAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        var attempt = await LoadAttemptAsync(attemptId, ct);

        // Students only see their own attempts
        if (attempt.Participant.StudentId != currentUser.Id)
            throw new NotFoundException("attempt_not_found", "Attempt not found.");
        return attempt;
    }

    private async Task<(InterviewAttempt Attempt, InterviewTurn Turn)> LoadOwnedTurnAsync(
        Guid attemptId, Guid turnId, CancellationToken ct)
    {
        var attempt = await db.InterviewAttempts.AsNoTracking()
            .Include(item => item.Participant)
            .Include(item => item.Responses).ThenInclude(response => response.Turns)
            .AsSplitQuery()
            .FirstOrDefaultAsync(item => item.Id == attemptId, ct)
            ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");
        if (attempt.Participant.StudentId != currentUser.Id)
            throw new NotFoundException("attempt_not_found", "Attempt not found.");
        var turn = attempt.Responses.SelectMany(response => response.Turns)
            .FirstOrDefault(candidate => candidate.Id == turnId)
            ?? throw new ConflictException("turn_not_current", "The submitted turn does not belong to this attempt.");
        return (attempt, turn);
    }

    private void EnsureAudioStudent()
    {
        if (!currentUser.IsAuthenticated)
            throw new UnauthorizedException("unauthenticated", "You need to sign in.");
        if (!currentUser.IsInRole(Roles.Student))
            throw new ForbiddenException("Only the student taking the attempt can submit audio.");
    }

    private void EnsureRecoveryActor()
    {
        if (!currentUser.IsAuthenticated)
            throw new UnauthorizedException("unauthenticated", "You need to sign in.");
        if (!currentUser.IsInRole(Roles.Administrator) && !currentUser.IsInRole(Roles.Lecturer))
            throw new ForbiddenException("Only administrators and assigned lecturers can recover turns.");
    }

    private async Task EnsureRecoveryActorAsync(Guid sessionId, CancellationToken ct)
    {
        EnsureRecoveryActor();
        if (currentUser.IsInRole(Roles.Lecturer)
            && !await examConfig.IsLecturerOfSessionAsync(currentUser.Id, sessionId, ct))
            throw new ForbiddenException("Only lecturers assigned to the session can recover turns.");
    }

    private async Task<InterviewAttempt> LoadAttemptAsync(Guid attemptId, CancellationToken ct) =>
        await db.InterviewAttempts
            .Include(a => a.Participant).ThenInclude(p => p.Attempts)
            .Include(a => a.Responses).ThenInclude(r => r.Turns)
            .Include(a => a.Responses).ThenInclude(r => r.CriterionScores)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct)
        ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");

    private async Task<ExamSessionInfo> GetSessionAsync(Guid sessionId, CancellationToken ct) =>
        await examConfig.GetSessionAsync(sessionId, ct)
        ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");

    private async Task<SubmitAnswerResponse> ProcessTurnAsync(
        InterviewAttempt attempt,
        QuestionResponse response,
        InterviewTurn turn,
        ExamSessionInfo session,
        string? answer,
        DateTime now,
        bool timedOut,
        bool sessionOpen,
        bool manualRecovery,
        List<InterviewAttemptEvent> events,
        CancellationToken ct)
    {
        var question = session.Questions.FirstOrDefault(q => q.Id == response.QuestionId)
                       ?? throw new ConflictException("attempt_question_not_found", "The attempt question is not in its exam session.");

        var turnCount = attempt.Responses.Sum(item => item.Turns.Count);
        var deadlineReached = now > GetTurnDeadline(turn, session);
        events.Add(CreateEvent(attempt, session.Id,
            timedOut ? deadlineReached ? "turn.deadline_reached" : "turn.closed" : "answer.accepted",
            InterviewEventSequence.TurnAnswered(turnCount), now));

        var dialogue = response.Turns.OrderBy(t => t.TurnNo)
            .Select(t => new DialogueTurn(t.QuestionText, t.AnswerTranscript, t.Type == TurnType.FollowUp))
            .ToList();
        var analysis = await analyzer.AnalyzeAsync(new AnswerAnalysisRequest(
            question.Content, question.Criteria, dialogue,
            response.FollowUpCount, session.MaxFollowUps, DefaultLanguage), ct);

        var followUpOpenedAt = UtcNow;
        if (!manualRecovery && !timedOut && sessionOpen && session.IsOpenAt(followUpOpenedAt)
            && analysis.NeedsFollowUp && response.FollowUpCount < session.MaxFollowUps
            && !string.IsNullOrWhiteSpace(analysis.FollowUpQuestion))
        {
            response.AddTurn(TurnType.FollowUp, analysis.FollowUpQuestion!, followUpOpenedAt);
            events.Add(CreateEvent(attempt, session.Id, "turn.opened", InterviewEventSequence.TurnOpened(turnCount + 1), followUpOpenedAt));
            logger.LogInformation("Attempt {AttemptId}: follow-up {Count} for question {QuestionId}",
                attempt.Id, response.FollowUpCount, question.Id);
            return new SubmitAnswerResponse(SubmitOutcome.FollowUp, timedOut, null, ToState(attempt, session));
        }

        var score = await scorer.ScoreAsync(new RubricScoringRequest(
            question.Content, question.Criteria, dialogue, DefaultLanguage), ct);
        var criterionScores = ValidateAiScore(score, question.Criteria)
            .Select((given, index) => new CriterionScore
            {
                Id = Guid.NewGuid(),
                QuestionResponseId = response.Id,
                RubricCriterionId = question.Criteria[index].Id,
                Points = given.Points!.Value,
                Comment = given.Comment,
            }).ToList();
        response.RecordAiEvaluation(criterionScores, score.Feedback, now);
        db.CriterionScores.AddRange(criterionScores);
        var suggested = response.AiSuggestedScore!.Value;

        var nextOpenedAt = UtcNow;
        var canAskNextQuestion = !manualRecovery && sessionOpen && session.IsOpenAt(nextOpenedAt);
        var next = canAskNextQuestion
            ? session.Questions.FirstOrDefault(q => q.OrderNo > question.OrderNo)
            : null;
        string outcome;
        if (next is not null)
        {
            AskMainQuestion(attempt, next, nextOpenedAt);
            events.Add(CreateEvent(attempt, session.Id, "turn.opened", InterviewEventSequence.TurnOpened(turnCount + 1), nextOpenedAt));
            outcome = SubmitOutcome.NextQuestion;
        }
        else
        {
            if (manualRecovery)
                attempt.CompleteRecovered(now);
            else
                attempt.Complete(now);
            attempt.Participant.ReconcileStatus();
            events.Add(CreateEvent(attempt, session.Id, canAskNextQuestion ? "attempt.completed" : "attempt.expired",
                InterviewEventSequence.AttemptCompleted(turnCount), now));
            outcome = SubmitOutcome.Completed;
        }

        logger.LogInformation("Attempt {AttemptId}: question {QuestionId} scored {Score}/{Max}, outcome {Outcome}",
            attempt.Id, question.Id, suggested, question.MaxScore, outcome);
        return new SubmitAnswerResponse(outcome, timedOut, null, ToState(attempt, session));
    }

    private static IReadOnlyList<CriterionPoints> ValidateAiScore(
        RubricScore? score,
        IReadOnlyList<RubricCriterionInfo> criteria)
    {
        var scores = score?.Scores;
        if (scores is null || scores.Count != criteria.Count
            || scores.Select(item => item.CriterionId).Distinct().Count() != scores.Count)
            throw new InvalidOperationException("AI rubric scoring returned an incomplete or duplicate criterion score set.");

        var byCriterion = scores.ToDictionary(item => item.CriterionId);
        if (criteria.Any(criterion => !byCriterion.TryGetValue(criterion.Id, out var item)
            || item.Points is not decimal points
            || points < 0
            || points > criterion.MaxPoints
            || points != decimal.Round(points, 2)))
            throw new InvalidOperationException("AI rubric scoring returned an invalid criterion score.");

        return criteria.Select(criterion => byCriterion[criterion.Id]).ToList();
    }

    private async Task<SubmitAnswerResponse> ProcessClaimedTurnAsync(
        Guid attemptId, TurnProcessingClaim claim, CancellationToken ct)
    {
        if (claim.Stage == TurnProcessingStage.AudioValidation)
            return await ProcessClaimedAudioValidationAsync(attemptId, claim, ct);
        if (claim.Stage == TurnProcessingStage.Transcription)
            return await ProcessClaimedAudioTranscriptionAsync(attemptId, claim, ct);
        if (claim.Stage is not (TurnProcessingStage.AiEvaluation or TurnProcessingStage.TimeoutEvaluation))
            throw new InvalidOperationException($"Unsupported turn processing stage: {claim.Stage}");
        var events = new List<InterviewAttemptEvent>();
        SubmitAnswerResponse result;
        try
        {
            result = await ProcessTurnAsync(claim.Attempt, claim.Response, claim.Turn, claim.Session,
                claim.Answer, claim.Now, claim.TimedOut, claim.SessionOpen, claim.ManualRecovery, events, ct);
            claim.Turn.ProcessingStatus = TurnProcessingStatus.Completed;
            claim.Turn.ProcessingStartedAt = null;
            claim.Turn.NextRetryAt = null;
            claim.Turn.ProcessingToken = null;
            claim.Turn.SubmissionResultJson = JsonSerializer.Serialize(result);
            await WithAttemptLockAsync(attemptId, async () =>
            {
                await EnsureProcessingLeaseAsync(claim.Turn.Id, claim.ProcessingToken, ct);
                return result;
            }, ct);
        }
        catch (TurnProcessingLeaseLostException)
        {
            return await GetCurrentClaimResponseAsync(attemptId, claim.Turn.Id);
        }
        catch
        {
            try
            {
                await WithAttemptLockAsync(attemptId, async () =>
                {
                    await EnsureProcessingLeaseAsync(claim.Turn.Id, claim.ProcessingToken, CancellationToken.None);
                    claim.Turn.RetryCount++;
                    claim.Turn.ProcessingStatus = claim.Turn.RetryCount > Math.Max(0, _options.MaximumTurnRetries)
                        ? TurnProcessingStatus.Failed
                        : TurnProcessingStatus.Retry;
                    claim.Turn.ProcessingStartedAt = null;
                    claim.Turn.ProcessingToken = null;
                    var retryNow = UtcNow;
                    claim.Turn.NextRetryAt = claim.Turn.ProcessingStatus == TurnProcessingStatus.Retry
                        ? retryNow.Add(CalculateRetryDelay(claim.Turn.RetryCount, _options))
                        : null;
                    if (claim.Turn.ProcessingStatus == TurnProcessingStatus.Failed)
                    {
                        if (claim.Attempt.Status == AttemptStatus.InProgress)
                        {
                            claim.Attempt.Interrupt(retryNow);
                            claim.Attempt.Participant.ReconcileStatus();
                        }
                    }
                    return true;
                }, CancellationToken.None);
            }
            catch (TurnProcessingLeaseLostException)
            {
                db.ChangeTracker.Clear();
                throw new ConflictException("turn_processing_reclaimed",
                    "Turn processing was recovered by another worker; retry the request.");
            }
            var sequence = InterviewEventSequence.Current(claim.Attempt);
            await eventPublisher.PublishAsync(new InterviewAttemptEvent(Guid.NewGuid(), attemptId,
                claim.Session.Id, sequence, claim.Turn.Id, sequence, "InterviewError", UtcNow), CancellationToken.None);
            throw;
        }

        await PublishEventsAsync(events, ct);
        if (claim.Turn.AudioAcceptedAt is not null && claim.Turn.AnswerTranscript is { } transcript)
            await eventPublisher.PublishTranscriptUpdatedAsync(
                new TranscriptUpdatedEvent(attemptId, claim.Turn.Id, transcript), ct);
        return result;
    }

    private async Task<SubmitAnswerResponse> ProcessClaimedAudioValidationAsync(
        Guid attemptId, TurnProcessingClaim claim, CancellationToken ct)
    {
        TurnProcessingClaim transcriptionClaim;
        try
        {
            var completed = await audioStore.CompleteUploadAsync(attemptId, claim.Turn.Id, ct);
            await completed.Audio.DisposeAsync();

            transcriptionClaim = await WithAttemptLockAsync(attemptId, async () =>
            {
                await EnsureProcessingLeaseAsync(claim.Turn.Id, claim.ProcessingToken, ct);
                var attemptStatus = await db.InterviewAttempts.AsNoTracking()
                    .Where(a => a.Id == attemptId).Select(a => a.Status).SingleAsync(ct);
                if (attemptStatus != AttemptStatus.InProgress
                    && !(claim.ManualRecovery && attemptStatus == AttemptStatus.Interrupted)
                    || claim.Turn.ProcessingStage != TurnProcessingStage.AudioValidation
                    || claim.Turn.AudioCompletionReceivedAt is null
                    || claim.Turn.AudioAcceptedAt is not null
                    || claim.Turn.AnsweredAt is not null || claim.Turn.IsTimedOut)
                    throw new TurnProcessingLeaseLostException();

                var now = UtcNow;
                claim.Turn.AudioAcceptedAt = claim.Turn.AudioCompletionReceivedAt.Value;
                claim.Turn.ProcessingStartedAt = now;
                claim.Turn.NextRetryAt = null;
                claim.Turn.ProcessingToken = Guid.NewGuid();
                return claim with
                {
                    Now = now,
                    SessionOpen = claim.Session.IsOpenAt(now),
                    ProcessingToken = claim.Turn.ProcessingToken.Value,
                    Stage = TurnProcessingStage.Transcription
                };
            }, ct);
        }
        catch (TurnProcessingLeaseLostException)
        {
            return await GetCurrentClaimResponseAsync(attemptId, claim.Turn.Id);
        }
        catch (Exception exception)
        {
            var missingAudio = exception is ConflictException { Code: "audio_upload_empty" };
            try
            {
                using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await WithAttemptLockAsync(attemptId, async () =>
                {
                    await EnsureProcessingLeaseAsync(claim.Turn.Id, claim.ProcessingToken, recoveryTimeout.Token);
                    var now = UtcNow;
                    if (missingAudio && !claim.ManualRecovery)
                    {
                        // A missing upload is not accepted. The caller may retry before deadline.
                        claim.Turn.AudioCompletionReceivedAt = null;
                        claim.Turn.ProcessingStatus = TurnProcessingStatus.AwaitingAnswer;
                        claim.Turn.NextRetryAt = null;
                    }
                    else
                    {
                        claim.Turn.RetryCount++;
                        var exhausted = claim.Turn.RetryCount > Math.Max(0, _options.MaximumTurnRetries);
                        claim.Turn.ProcessingStatus = exhausted
                            ? TurnProcessingStatus.Failed : TurnProcessingStatus.Retry;
                        claim.Turn.NextRetryAt = exhausted ? null
                            : now.Add(CalculateRetryDelay(claim.Turn.RetryCount, _options));
                        if (exhausted)
                        {
                            if (claim.Attempt.Status == AttemptStatus.InProgress)
                            {
                                claim.Attempt.Interrupt(now);
                                claim.Attempt.Participant.ReconcileStatus();
                            }
                        }
                    }
                    claim.Turn.ProcessingStartedAt = null;
                    claim.Turn.ProcessingToken = null;
                    return true;
                }, recoveryTimeout.Token);
            }
            catch (TurnProcessingLeaseLostException)
            {
                db.ChangeTracker.Clear();
                throw new ConflictException("turn_processing_reclaimed",
                    "Turn processing was recovered by another worker; retry the request.");
            }
            catch (Exception recoveryException)
            {
                logger.LogError(recoveryException,
                    "Failed to checkpoint audio validation for attempt {AttemptId}, turn {TurnId}.",
                    attemptId, claim.Turn.Id);
            }
            throw;
        }
        return await ProcessClaimedTurnAsync(attemptId, transcriptionClaim, ct);
    }

    private async Task<SubmitAnswerResponse> ProcessClaimedAudioTranscriptionAsync(
        Guid attemptId, TurnProcessingClaim claim, CancellationToken ct)
    {
        TurnProcessingClaim evaluationClaim;
        try
        {
            var completed = await audioStore.CompleteUploadAsync(attemptId, claim.Turn.Id, ct);
            string transcript;
            await using (completed.Audio)
            {
                transcript = await speechToText.TranscribeAsync(
                    completed.Audio, completed.ContentType, DefaultLanguage, ct)
                    ?? throw new AppException(502, "speech_to_text_invalid_response",
                        "Speech-to-text returned no transcript result.");
            }

            if (transcript.Length > 10000)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    [nameof(SubmitAnswerRequest.AnswerText)] = ["Transcribed answers cannot exceed 10000 characters."]
                });

            evaluationClaim = await WithAttemptLockAsync(attemptId, async () =>
            {
                await EnsureProcessingLeaseAsync(claim.Turn.Id, claim.ProcessingToken, ct);
                var now = UtcNow;
                var attemptStatus = await db.InterviewAttempts.AsNoTracking()
                    .Where(attempt => attempt.Id == attemptId)
                    .Select(attempt => attempt.Status)
                    .SingleAsync(ct);
                if (claim.Turn.ProcessingStage != TurnProcessingStage.Transcription
                    || claim.Turn.AudioAcceptedAt is null
                    || attemptStatus != AttemptStatus.InProgress
                        && !(claim.ManualRecovery && attemptStatus == AttemptStatus.Interrupted))
                    throw new TurnProcessingLeaseLostException();

                claim.Turn.AnswerTranscript = transcript;
                claim.Turn.AnsweredAt = claim.Turn.AudioAcceptedAt;
                claim.Turn.ProcessingStartedAt = now;
                claim.Turn.NextRetryAt = null;
                claim.Turn.ProcessingToken = Guid.NewGuid();
                var token = claim.Turn.ProcessingToken.Value;
                return claim with
                {
                    Answer = transcript,
                    Now = now,
                    SessionOpen = claim.Session.IsOpenAt(now),
                    ProcessingToken = token,
                    Stage = TurnProcessingStage.AiEvaluation
                };
            }, ct);
        }
        catch (TurnProcessingLeaseLostException)
        {
            return await GetCurrentClaimResponseAsync(attemptId, claim.Turn.Id);
        }
        catch (Exception exception)
        {
            var missingAudio = exception is ConflictException { Code: "audio_upload_empty" };
            try
            {
                await WithAttemptLockAsync(attemptId, async () =>
                {
                    await EnsureProcessingLeaseAsync(claim.Turn.Id, claim.ProcessingToken, CancellationToken.None);
                    var retryCount = missingAudio
                        ? claim.Turn.TranscriptionRetryCount
                        : ++claim.Turn.TranscriptionRetryCount;
                    claim.Turn.ProcessingStatus = missingAudio
                        || retryCount > Math.Max(0, _options.MaximumTurnRetries)
                        ? TurnProcessingStatus.Failed
                        : TurnProcessingStatus.Retry;
                    claim.Turn.ProcessingStartedAt = null;
                    claim.Turn.ProcessingToken = null;
                    var retryNow = UtcNow;
                    claim.Turn.NextRetryAt = claim.Turn.ProcessingStatus == TurnProcessingStatus.Retry
                        ? retryNow.Add(CalculateRetryDelay(retryCount, _options))
                        : null;
                    if (claim.Turn.ProcessingStatus == TurnProcessingStatus.Failed)
                    {
                        if (claim.Attempt.Status == AttemptStatus.InProgress)
                        {
                            claim.Attempt.Interrupt(retryNow);
                            claim.Attempt.Participant.ReconcileStatus();
                        }
                    }
                    return true;
                }, CancellationToken.None);
            }
            catch (TurnProcessingLeaseLostException)
            {
                db.ChangeTracker.Clear();
                throw new ConflictException("turn_processing_reclaimed",
                    "Turn processing was recovered by another worker; retry the request.");
            }

            var sequence = InterviewEventSequence.Current(claim.Attempt);
            await eventPublisher.PublishAsync(new InterviewAttemptEvent(Guid.NewGuid(), attemptId,
                claim.Session.Id, sequence, claim.Turn.Id, sequence, "InterviewError", UtcNow), CancellationToken.None);
            throw;
        }

        return await ProcessClaimedTurnAsync(attemptId, evaluationClaim, ct);
    }

    private async Task<SubmitAnswerResponse> GetCurrentClaimResponseAsync(Guid attemptId, Guid turnId)
    {
        db.ChangeTracker.Clear();
        var currentAttempt = await LoadAttemptAsync(attemptId, CancellationToken.None);
        var currentTurn = currentAttempt.Responses.SelectMany(response => response.Turns)
            .Single(turn => turn.Id == turnId);
        var currentResponse = currentAttempt.Responses.Single(response => response.Id == currentTurn.QuestionResponseId);
        var currentSession = await GetSessionAsync(currentAttempt.Participant.ExamSessionId, CancellationToken.None);
        return GetOriginalResponse(currentAttempt, currentResponse, currentTurn, currentSession);
    }

    private sealed record TurnProcessingClaim(
        InterviewAttempt Attempt,
        QuestionResponse Response,
        InterviewTurn Turn,
        ExamSessionInfo Session,
        string? Answer,
        DateTime Now,
        bool TimedOut,
        bool SessionOpen,
        Guid ProcessingToken,
        TurnProcessingStage Stage = TurnProcessingStage.AiEvaluation,
        bool ManualRecovery = false,
        RecoveryAuditData? RecoveryAudit = null);

    private sealed record RecoveryAuditData(
        Guid AttemptId,
        Guid TurnId,
        string PreviousAttemptState,
        string PreviousTurnState,
        int RetryCount,
        int TranscriptionRetryCount,
        string ResumedStage);

    private async Task WriteRecoveryAuditAsync(
        RecoveryAuditData audit, string action, string outcome, CancellationToken ct)
    {
        var description = JsonSerializer.Serialize(new
        {
            attempt = audit.AttemptId,
            turn = audit.TurnId,
            reason = "Interrupted turn manual recovery",
            previousAttemptState = audit.PreviousAttemptState,
            previousTurnState = audit.PreviousTurnState,
            retryCount = audit.RetryCount,
            transcriptionRetryCount = audit.TranscriptionRetryCount,
            resumedStage = audit.ResumedStage,
            outcome
        });
        await auditLogWriter.AppendAsync(new AuditLogEntry(
            currentUser.Id, action, AuditLogEntities.InterviewAttempt, audit.AttemptId, description), ct);
    }

    internal static TimeSpan CalculateRetryDelay(int retryCount, InterviewOptions options)
    {
        var maximumSeconds = Math.Max(0, options.MaximumTurnRetryDelaySeconds);
        var delaySeconds = Math.Clamp(options.InitialTurnRetryDelaySeconds, 0, maximumSeconds);
        for (var retry = 1; retry < retryCount && delaySeconds < maximumSeconds; retry++)
            delaySeconds = Math.Min(maximumSeconds, delaySeconds * 2);
        return TimeSpan.FromSeconds(delaySeconds);
    }

    private sealed record TurnSubmissionClaim(
        TurnProcessingClaim? Processing,
        SubmitAnswerResponse? Response,
        InterviewAttemptEvent? Event = null);

    private sealed record AudioSubmissionClaim(
        TurnProcessingClaim? Processing,
        SubmitAnswerResponse? Response,
        bool PendingUploadValidation = false);

    private SubmitAnswerResponse DuplicateResponse(
        InterviewAttempt attempt,
        QuestionResponse submittedResponse,
        InterviewTurn submittedTurn,
        ExamSessionInfo session)
    {
        var currentResponse = attempt.CurrentResponse;
        var outcome = attempt.Status != AttemptStatus.InProgress
            ? SubmitOutcome.Completed
            : currentResponse?.Id == submittedResponse.Id && currentResponse.CurrentTurn is not null
                ? SubmitOutcome.FollowUp
                : SubmitOutcome.NextQuestion;
        logger.LogInformation("Attempt {AttemptId}: duplicate submission for turn {TurnId} ignored",
            attempt.Id, submittedTurn.Id);
        return new SubmitAnswerResponse(outcome, false, null, ToState(attempt, session), Duplicate: true);
    }

    private SubmitAnswerResponse GetOriginalResponse(
        InterviewAttempt attempt,
        QuestionResponse submittedResponse,
        InterviewTurn submittedTurn,
        ExamSessionInfo session)
    {
        if (submittedTurn.SubmissionResultJson is { Length: > 0 } resultJson)
        {
            var original = JsonSerializer.Deserialize<SubmitAnswerResponse>(resultJson);
            if (original is not null)
                return original with { Duplicate = true };
        }

        return DuplicateResponse(attempt, submittedResponse, submittedTurn, session);
    }

    private static string? NormalizeAnswer(string? answer) => answer?.Trim();

    private async Task EnsureProcessingLeaseAsync(Guid turnId, Guid processingToken, CancellationToken ct)
    {
        var storedToken = await db.InterviewTurns.AsNoTracking()
            .Where(turn => turn.Id == turnId)
            .Select(turn => turn.ProcessingToken)
            .SingleOrDefaultAsync(ct);
        if (storedToken != processingToken)
            throw new TurnProcessingLeaseLostException();
    }

    private static InterviewAttemptEvent CreateEvent(
        InterviewAttempt attempt, Guid sessionId, string type, long sequence, DateTime occurredAt, Guid? turnId = null)
    {
        turnId ??= attempt.Responses.SelectMany(response => response.Turns)
            .MaxBy(turn => turn.AskedAt)?.Id;
        return new InterviewAttemptEvent(Guid.NewGuid(), attempt.Id, sessionId, sequence,
            turnId, sequence, type, occurredAt);
    }

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

    private sealed class TurnProcessingLeaseLostException : Exception
    {
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
        var response = attempt.Status == AttemptStatus.InProgress && session.IsOpenAt(UtcNow)
            ? attempt.CurrentResponse
            : null;
        var turn = response?.CurrentTurn;
        if (turn is not null && UtcNow > GetTurnDeadline(turn, session))
            turn = null;
        if (response is not null && turn is not null)
        {
            var q = session.Questions.First(x => x.Id == response.QuestionId);
            current = new CurrentTurnDto(
                response.Id, q.Id, q.OrderNo, turn.TurnNo, turn.Type.ToString(), turn.QuestionText,
                turn.AskedAt, GetTurnDeadline(turn, session), turn.Id);
        }

        return new AttemptStateDto(
            attempt.Id, session.Id, session.Title, attempt.Status.ToString(),
            session.TimeLimitPerAnswerSeconds, session.MaxFollowUps, session.Questions.Count,
            attempt.Responses.Count(r => r.IsFinished),
            attempt.StartedAt, attempt.CompletedAt, current, InterviewEventSequence.Current(attempt));
    }
}
