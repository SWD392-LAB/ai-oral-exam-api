using System.Text.Json;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Application.Audio;
using AiOralExam.Modules.Interview.Application.Events;
using AiOralExam.Modules.Interview.Application.Features.ScoreReview;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOralExam.UnitTests;

public sealed class InterviewTurnWorkflowTests
{
    private static readonly DateTime StartTime = new(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task First_answer_is_saved_and_turn_id_retry_is_idempotent()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = Assert.IsType<Guid>(started.State.CurrentTurn?.TurnId);

        var first = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("A clear answer", turnId));
        var retry = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("A clear answer", turnId));

        Assert.False(first.Duplicate);
        Assert.Null(first.Evaluation);
        Assert.True(retry.Duplicate);
        Assert.Null(retry.Evaluation);
        Assert.Equal(first.Attempt.AttemptId, retry.Attempt.AttemptId);
        Assert.Equal(AttemptStatus.PendingReview.ToString(), retry.Attempt.Status);
        Assert.Single(fixture.Db.InterviewAttempts);
        Assert.Single(fixture.Db.QuestionResponses);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Equal(1, fixture.Analyzer.CallCount);
        Assert.Equal(1, fixture.Scorer.CallCount);
        Assert.Equal(["attempt.started", "turn.opened", "answer.accepted", "attempt.completed", "answer.duplicate"],
            fixture.Events.Events.Select(interviewEvent => interviewEvent.Type));
        Assert.Equal([1L, 2L, 3L, 4L, 4L], fixture.Events.Events.Select(interviewEvent => interviewEvent.Sequence));
        Assert.All(fixture.Events.Events, interviewEvent =>
            Assert.Equal(new[] { "AttemptId", "EventId", "ExamSessionId", "OccurredAt", "Sequence", "StateVersion", "TurnId", "Type" },
                typeof(InterviewAttemptEvent).GetProperties().Select(property => property.Name).OrderBy(name => name)));
        Assert.DoesNotContain("AnswerTranscript", typeof(InterviewAttemptEvent).GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("AiSuggestedScore", typeof(InterviewAttemptEvent).GetProperties().Select(property => property.Name));
        Assert.All(fixture.Events.Events.Where(interviewEvent => interviewEvent.Type != "review.finalized"),
            interviewEvent => Assert.Equal(interviewEvent.Sequence, interviewEvent.StateVersion));
    }

    [Fact]
    public async Task Delayed_identical_retry_returns_the_original_turn_result_after_the_attempt_advances()
    {
        await using var fixture = await CreateFixtureAsync(maxFollowUps: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var mainTurnId = started.State.CurrentTurn!.TurnId;
        fixture.Analyzer.NeedsFollowUp = true;

        var original = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Main answer", mainTurnId));
        Assert.Equal(SubmitOutcome.FollowUp, original.Outcome);
        var originalFollowUpId = original.Attempt.CurrentTurn!.TurnId;

        fixture.Analyzer.NeedsFollowUp = false;
        await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Follow-up answer", originalFollowUpId));
        var retry = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Main answer", mainTurnId));

        Assert.True(retry.Duplicate);
        Assert.Equal(original.Outcome, retry.Outcome);
        Assert.Equal(original.Attempt.CurrentTurn?.TurnId, retry.Attempt.CurrentTurn?.TurnId);
        Assert.Equal(mainTurnId, fixture.Events.Events[^1].TurnId);
        Assert.Equal(2, fixture.Analyzer.CallCount);
        Assert.Single(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Student_cannot_invoke_manual_recovery_at_the_application_boundary()
    {
        await using var fixture = await CreateFixtureAsync();

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Service
            .RecoverInterruptedTurnAsync(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Contains("administrators", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unassigned_lecturer_cannot_recover_an_attempt()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var lecturerId = Guid.NewGuid();
        fixture.Configuration.AssignedLecturerId = Guid.NewGuid();

        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.CreateService(
                new TestUser(lecturerId, Roles.Lecturer))
            .RecoverInterruptedTurnAsync(started.State.AttemptId, started.State.CurrentTurn!.TurnId));
    }

    [Fact]
    public async Task Reclaimed_processing_lease_prevents_an_older_provider_result_from_committing()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Analyzer.PauseNextCall();
        var originalProcessing = fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Durable while reclaimed", started.State.CurrentTurn!.TurnId));
        await fixture.Analyzer.WaitUntilPausedAsync();

        await using var recoveryDb = fixture.CreateIndependentDb();
        var recoveryService = fixture.CreateIndependentService(recoveryDb);
        fixture.Clock.Set(StartTime.AddMinutes(3));
        Assert.Equal(0, await recoveryService.ExpireOverdueAttemptsAsync());
        var scheduledTurn = await recoveryDb.InterviewTurns.SingleAsync();
        Assert.Equal(TurnProcessingStatus.Retry, scheduledTurn.ProcessingStatus);
        Assert.Null(scheduledTurn.ProcessingToken);
        Assert.Equal(StartTime.AddMinutes(3).AddSeconds(5), scheduledTurn.NextRetryAt);

        fixture.Analyzer.Release();
        var lateProviderResult = await originalProcessing;

        Assert.True(lateProviderResult.Duplicate);
        fixture.Clock.Set(scheduledTurn.NextRetryAt!.Value);
        Assert.Equal(1, await recoveryService.ExpireOverdueAttemptsAsync());
        await using var verificationDb = fixture.CreateIndependentDb();
        Assert.Equal(TurnProcessingStatus.Completed, (await verificationDb.InterviewTurns.SingleAsync()).ProcessingStatus);
        Assert.Single(await verificationDb.CriterionScores.ToListAsync());
        Assert.Equal(AttemptStatus.PendingReview, (await verificationDb.InterviewAttempts.SingleAsync()).Status);
    }

    [Fact]
    public async Task Scorer_failure_is_recovered_by_the_worker_without_losing_the_answer()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Scorer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Persist before score", started.State.CurrentTurn!.TurnId)));

        Assert.Equal("Persist before score", fixture.Db.InterviewTurns.Single().AnswerTranscript);
        Assert.Equal(TurnProcessingStatus.Retry, fixture.Db.InterviewTurns.Single().ProcessingStatus);
        Assert.Equal(StartTime.AddSeconds(5), fixture.Db.InterviewTurns.Single().NextRetryAt);
        Assert.Empty(fixture.Db.CriterionScores);

        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
        fixture.Clock.Set(StartTime.AddSeconds(5));
        Assert.Equal(1, await fixture.Service.ExpireOverdueAttemptsAsync());

        Assert.Equal(TurnProcessingStatus.Completed, fixture.Db.InterviewTurns.Single().ProcessingStatus);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Equal(2, fixture.Scorer.CallCount);
    }

    [Fact]
    public async Task Missing_or_invalid_criterion_scores_fail_evaluation_without_persisting_a_score()
    {
        Func<Guid, RubricScore>[] invalidScoreFactories =
        {
            _ => new([], "Missing all scores."),
            criterionId => new([new CriterionPoints(criterionId, null, null)], "Missing points."),
            criterionId => new([new CriterionPoints(criterionId, -1m, null)], "Negative points."),
            criterionId => new([new CriterionPoints(criterionId, 10.01m, null)], "Out of range."),
            _ => new([new CriterionPoints(Guid.NewGuid(), 5m, null)], "Unknown criterion."),
            criterionId => new([new CriterionPoints(criterionId, 5m, null), new CriterionPoints(criterionId, 5m, null)], "Duplicate criterion."),
        };

        foreach (var createInvalidScore in invalidScoreFactories)
        {
            await using var fixture = await CreateFixtureAsync();
            var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
            fixture.Scorer.ResultOverride = createInvalidScore(fixture.Session.Questions[0].Criteria[0].Id);

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
                started.State.AttemptId,
                new SubmitAnswerRequest("Answer", started.State.CurrentTurn!.TurnId)));

            Assert.Empty(fixture.Db.CriterionScores);
            Assert.Null(fixture.Db.QuestionResponses.Single().AiSuggestedScore);
            Assert.Equal(TurnProcessingStatus.Retry, fixture.Db.InterviewTurns.Single().ProcessingStatus);
            Assert.Equal(AttemptStatus.InProgress, fixture.Db.InterviewAttempts.Single().Status);
        }
    }

    [Theory]
    [InlineData("\"not-a-number\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public async Task Malformed_and_non_finite_scores_fail_evaluation(string points)
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var criterionId = fixture.Session.Questions[0].Criteria[0].Id;
        fixture.Scorer.RawJsonOverride = $$"""{"scores":[{"criterionId":"{{criterionId}}","points":{{points}},"comment":null}],"feedback":""}""";

        await Assert.ThrowsAsync<JsonException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId,
            new SubmitAnswerRequest("Answer", started.State.CurrentTurn!.TurnId)));

        Assert.Empty(fixture.Db.CriterionScores);
        Assert.Null(fixture.Db.QuestionResponses.Single().AiSuggestedScore);
        Assert.Equal(TurnProcessingStatus.Retry, fixture.Db.InterviewTurns.Single().ProcessingStatus);
    }

    [Fact]
    public async Task Missing_or_empty_turn_id_is_rejected_without_falling_back_to_current_turn()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var error = await Assert.ThrowsAsync<ValidationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Answer", Guid.Empty)));

        Assert.Contains(nameof(SubmitAnswerRequest.TurnId), error.Details!.Keys);
        Assert.Null(fixture.Db.InterviewTurns.Single().AnsweredAt);
        Assert.Equal(0, fixture.Analyzer.CallCount);
    }

    [Fact]
    public async Task Get_state_reconciles_an_expired_turn_when_the_worker_has_not_run()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(2));

        var state = await fixture.Service.GetAttemptStateAsync(started.State.AttemptId);

        Assert.Null(state.CurrentTurn);
        Assert.Equal(AttemptStatus.PendingReview.ToString(), state.Status);
        Assert.True(fixture.Db.InterviewTurns.Single().IsTimedOut);
        Assert.Null(fixture.Db.InterviewTurns.Single().AnsweredAt);
    }

    [Fact]
    public async Task Get_state_expires_only_the_requesting_students_attempt()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        var ownAttempt = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var otherStudentId = Guid.NewGuid();
        fixture.Db.Participants.Add(new Participant
        {
            Id = Guid.NewGuid(),
            ExamSessionId = fixture.Session.Id,
            StudentId = otherStudentId,
            JoinedAt = StartTime,
            Status = ParticipantStatus.Registered
        });
        await fixture.Db.SaveChangesAsync();
        var otherAttempt = await fixture.CreateService(new TestUser(otherStudentId, Roles.Student))
            .StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(2));

        var ownState = await fixture.Service.GetAttemptStateAsync(ownAttempt.State.AttemptId);

        Assert.Equal(AttemptStatus.PendingReview.ToString(), ownState.Status);
        Assert.Null(ownState.CurrentTurn);
        Assert.Equal(AttemptStatus.InProgress, fixture.Db.InterviewAttempts.Single(a => a.Id == otherAttempt.State.AttemptId).Status);
        Assert.Equal(TurnProcessingStatus.AwaitingAnswer,
            fixture.Db.InterviewTurns.Single(t => t.QuestionResponseId == otherAttempt.State.CurrentTurn!.QuestionResponseId).ProcessingStatus);
    }

    [Fact]
    public void Retry_delay_doubles_and_stays_within_configured_boundaries()
    {
        var options = new InterviewOptions
        {
            InitialTurnRetryDelaySeconds = 5,
            MaximumTurnRetryDelaySeconds = 12
        };

        Assert.Equal(TimeSpan.FromSeconds(5), InterviewService.CalculateRetryDelay(1, options));
        Assert.Equal(TimeSpan.FromSeconds(10), InterviewService.CalculateRetryDelay(2, options));
        Assert.Equal(TimeSpan.FromSeconds(12), InterviewService.CalculateRetryDelay(3, options));
        Assert.Equal(TimeSpan.FromSeconds(12), InterviewService.CalculateRetryDelay(4, options));
        var cappedOptions = new InterviewOptions
        {
            InitialTurnRetryDelaySeconds = 5,
            MaximumTurnRetryDelaySeconds = 3
        };
        Assert.Equal(TimeSpan.FromSeconds(3), InterviewService.CalculateRetryDelay(1, cappedOptions));
    }

    [Fact]
    public async Task Turn_processing_becomes_terminal_after_configured_retries_and_is_not_reclaimed()
    {
        await using var fixture = await CreateFixtureAsync(
            maximumTurnRetries: 1, initialRetryDelaySeconds: 0, maximumRetryDelaySeconds: 0);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Scorer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Retry me", started.State.CurrentTurn!.TurnId)));
        var turn = Assert.Single(fixture.Db.InterviewTurns);
        Assert.Equal(TurnProcessingStatus.Retry, turn.ProcessingStatus);
        Assert.Equal(1, turn.RetryCount);
        Assert.Equal(StartTime, turn.NextRetryAt);

        fixture.Scorer.FailNext = true;
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());

        Assert.Equal(TurnProcessingStatus.Failed, turn.ProcessingStatus);
        Assert.Equal(2, turn.RetryCount);
        Assert.Null(turn.NextRetryAt);
        Assert.Equal(AttemptStatus.Interrupted, fixture.Db.InterviewAttempts.Single().Status);
        Assert.NotNull(fixture.Db.InterviewAttempts.Single().CompletedAt);
        Assert.Equal(ParticipantStatus.Registered, fixture.Db.Participants.Single().Status);
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
        Assert.Equal(2, fixture.Scorer.CallCount);
        Assert.Empty(fixture.Db.CriterionScores);
        Assert.Empty(fixture.Db.ScoreReviews);

        var replacement = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));

        Assert.True(replacement.Created);
        Assert.Equal(2, fixture.Db.InterviewAttempts.Count());
        Assert.Equal(AttemptStatus.Interrupted, fixture.Db.InterviewAttempts.Single(a => a.Id == started.State.AttemptId).Status);
        Assert.Equal(AttemptStatus.InProgress, fixture.Db.InterviewAttempts.Single(a => a.Id == replacement.State.AttemptId).Status);
        Assert.Equal(ParticipantStatus.InProgress, fixture.Db.Participants.Single().Status);
        Assert.Empty(fixture.Db.CriterionScores);
        Assert.Empty(fixture.Db.ScoreReviews);
    }

    [Fact]
    public async Task Manual_recovery_evaluates_a_failed_turn_without_reopening_student_flow()
    {
        await using var fixture = await CreateFixtureAsync(maximumTurnRetries: 0, questionCount: 2);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        fixture.Scorer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Accepted answer", turnId)));

        var recovery = await fixture.CreateService(new TestUser(Guid.NewGuid(), Roles.Lecturer))
            .RecoverInterruptedTurnAsync(started.State.AttemptId, turnId);

        Assert.Equal(SubmitOutcome.Completed, recovery.Outcome);
        Assert.Equal(AttemptStatus.PendingReview.ToString(), recovery.Attempt.Status);
        Assert.Equal(TurnProcessingStatus.Completed, fixture.Db.InterviewTurns.Single().ProcessingStatus);
        Assert.Equal(1, fixture.Db.InterviewTurns.Single().ManualRecoveryCount);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Null(recovery.Attempt.CurrentTurn);
    }

    [Fact]
    public async Task Manual_audio_recovery_after_session_close_requires_durable_audio_and_preserves_acceptance()
    {
        await using var fixture = await CreateFixtureAsync(maximumTurnRetries: 0, timeLimitSeconds: 10);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");
        fixture.SpeechToText.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => audio.CompleteAudioAnswerAsync(
            started.State.AttemptId, turnId));

        var failed = fixture.Db.InterviewTurns.Single();
        var acceptedAt = failed.AudioAcceptedAt;
        var receiptAt = failed.AudioCompletionReceivedAt;
        fixture.Clock.Set(fixture.Session.EndTime.AddTicks(1));

        var recovery = await fixture.CreateService(new TestUser(Guid.NewGuid(), Roles.Lecturer))
            .RecoverInterruptedTurnAsync(started.State.AttemptId, turnId);

        var recovered = fixture.Db.InterviewTurns.Single();
        Assert.Equal(SubmitOutcome.Completed, recovery.Outcome);
        Assert.Equal(AttemptStatus.PendingReview.ToString(), recovery.Attempt.Status);
        Assert.Equal(TurnProcessingStatus.Completed, recovered.ProcessingStatus);
        Assert.Equal(receiptAt, recovered.AudioCompletionReceivedAt);
        Assert.Equal(acceptedAt, recovered.AudioAcceptedAt);
        Assert.Equal(acceptedAt, recovered.AnsweredAt);
        Assert.Equal("transcribed answer", recovered.AnswerTranscript);
        Assert.Single(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Concurrent_manual_recovery_requests_allow_only_the_failed_turn_claim()
    {
        await using var fixture = await CreateFixtureAsync(maximumTurnRetries: 0);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        fixture.Scorer.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Accepted answer", turnId)));

        var lecturer = new TestUser(Guid.NewGuid(), Roles.Lecturer);
        var first = fixture.CreateService(lecturer).RecoverInterruptedTurnAsync(started.State.AttemptId, turnId);
        var second = fixture.CreateService(lecturer).RecoverInterruptedTurnAsync(started.State.AttemptId, turnId);
        var results = await Task.WhenAll(
            first.ContinueWith(task => (Exception?)task.Exception?.InnerException),
            second.ContinueWith(task => (Exception?)task.Exception?.InnerException));

        Assert.Single(results, exception => exception is null);
        Assert.Single(results, exception => exception is ConflictException conflict
            && conflict.Code is "attempt_not_interrupted" or "turn_not_failed" or "manual_recovery_already_used");
        Assert.Equal(1, fixture.Db.InterviewTurns.Single().ManualRecoveryCount);
        Assert.Single(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Three_configured_retries_allow_four_total_executions_and_never_a_fifth()
    {
        await using var fixture = await CreateFixtureAsync(maximumTurnRetries: 3);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Scorer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Retry limit", started.State.CurrentTurn!.TurnId)));

        var turn = Assert.Single(fixture.Db.InterviewTurns);
        Assert.Equal(1, fixture.Scorer.CallCount);
        for (var retry = 0; retry < 3; retry++)
        {
            fixture.Clock.Set(turn.NextRetryAt!.Value);
            fixture.Scorer.FailNext = true;
            Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
        }

        Assert.Equal(TurnProcessingStatus.Failed, turn.ProcessingStatus);
        Assert.Equal(4, turn.RetryCount);
        Assert.Null(turn.NextRetryAt);
        Assert.Null(turn.ProcessingToken);
        Assert.Equal(4, fixture.Scorer.CallCount);
        Assert.Equal(AttemptStatus.Interrupted, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal(ParticipantStatus.Registered, fixture.Db.Participants.Single().Status);
        var state = await fixture.Service.GetAttemptStateAsync(started.State.AttemptId);
        Assert.Equal(AttemptStatus.Interrupted.ToString(), state.Status);
        Assert.Null(state.CurrentTurn);
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
        Assert.Equal(4, fixture.Scorer.CallCount);
    }

    [Fact]
    public async Task Stale_processing_is_scheduled_with_backoff_before_recovery_claim()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turn = Assert.Single(fixture.Db.InterviewTurns);
        turn.AnswerTranscript = "Stale answer";
        turn.AnsweredAt = StartTime;
        turn.ProcessingStatus = TurnProcessingStatus.Processing;
        turn.ProcessingStartedAt = StartTime;
        turn.ProcessingToken = Guid.NewGuid();
        fixture.Clock.Set(StartTime.AddMinutes(3));

        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());

        Assert.Equal(TurnProcessingStatus.Retry, turn.ProcessingStatus);
        Assert.Equal(1, turn.RetryCount);
        Assert.Null(turn.ProcessingStartedAt);
        Assert.Null(turn.ProcessingToken);
        Assert.Equal(StartTime.AddMinutes(3).AddSeconds(5), turn.NextRetryAt);
        Assert.Equal(0, fixture.Analyzer.CallCount);
        Assert.Equal(0, fixture.Scorer.CallCount);

        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
        Assert.Equal(0, fixture.Analyzer.CallCount);
        fixture.Clock.Set(turn.NextRetryAt!.Value);

        Assert.Equal(1, await fixture.Service.ExpireOverdueAttemptsAsync());

        Assert.Equal(TurnProcessingStatus.Completed, turn.ProcessingStatus);
        Assert.Equal(1, fixture.Analyzer.CallCount);
        Assert.Equal(1, fixture.Scorer.CallCount);
        Assert.Equal(1, turn.RetryCount);
    }

    [Fact]
    public async Task Exhausted_stale_processing_interrupts_attempt_without_evaluating_it()
    {
        await using var fixture = await CreateFixtureAsync(maximumTurnRetries: 3);
        await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turn = Assert.Single(fixture.Db.InterviewTurns);
        turn.AnswerTranscript = "Stale answer";
        turn.AnsweredAt = StartTime;
        turn.ProcessingStatus = TurnProcessingStatus.Processing;
        turn.ProcessingStartedAt = StartTime;
        turn.ProcessingToken = Guid.NewGuid();
        turn.RetryCount = 3;
        fixture.Clock.Set(StartTime.AddMinutes(3));

        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());

        Assert.Equal(TurnProcessingStatus.Failed, turn.ProcessingStatus);
        Assert.Equal(4, turn.RetryCount);
        Assert.Null(turn.ProcessingToken);
        Assert.Equal(AttemptStatus.Interrupted, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal(ParticipantStatus.Registered, fixture.Db.Participants.Single().Status);
        Assert.Empty(fixture.Db.CriterionScores);
        Assert.Equal(0, fixture.Analyzer.CallCount);
        Assert.Equal(0, fixture.Scorer.CallCount);
    }

    [Fact]
    public async Task Expiry_sweep_interrupts_an_attempt_with_an_expired_failed_turn()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turn = Assert.Single(fixture.Db.InterviewTurns);
        turn.ProcessingStatus = TurnProcessingStatus.Failed;
        turn.RetryCount = 3;
        fixture.Clock.Set(StartTime.AddSeconds(2));

        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());

        Assert.Equal(TurnProcessingStatus.Failed, turn.ProcessingStatus);
        Assert.Equal(3, turn.RetryCount);
        Assert.Null(turn.ProcessingToken);
        Assert.Equal(0, fixture.Analyzer.CallCount);
        Assert.Equal(0, fixture.Scorer.CallCount);
        Assert.Equal(AttemptStatus.Interrupted, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal(ParticipantStatus.Registered, fixture.Db.Participants.Single().Status);
    }

    [Fact]
    public async Task Student_get_reconciles_a_failed_turn_to_an_interrupted_attempt()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turn = Assert.Single(fixture.Db.InterviewTurns);
        turn.ProcessingStatus = TurnProcessingStatus.Failed;
        turn.RetryCount = 2;
        fixture.Clock.Set(StartTime.AddSeconds(2));

        var state = await fixture.Service.GetAttemptStateAsync(started.State.AttemptId);

        Assert.Null(state.CurrentTurn);
        Assert.Equal(AttemptStatus.Interrupted.ToString(), state.Status);
        Assert.Equal(TurnProcessingStatus.Failed, turn.ProcessingStatus);
        Assert.Equal(2, turn.RetryCount);
        Assert.Null(turn.ProcessingToken);
        Assert.Equal(0, fixture.Analyzer.CallCount);
        Assert.Equal(0, fixture.Scorer.CallCount);
        Assert.Equal(AttemptStatus.Interrupted, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal(ParticipantStatus.Registered, fixture.Db.Participants.Single().Status);
    }

    [Fact]
    public async Task Deadline_worker_interrupts_an_attempt_with_a_terminal_failed_turn()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turn = Assert.Single(fixture.Db.InterviewTurns);
        turn.ProcessingStatus = TurnProcessingStatus.Failed;
        turn.RetryCount = 2;
        fixture.Clock.Set(StartTime.AddSeconds(2));

        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());

        Assert.Equal(TurnProcessingStatus.Failed, turn.ProcessingStatus);
        Assert.Equal(2, turn.RetryCount);
        Assert.Null(turn.ProcessingToken);
        Assert.Equal(0, fixture.Analyzer.CallCount);
        Assert.Equal(0, fixture.Scorer.CallCount);
        Assert.Equal(AttemptStatus.Interrupted, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal(ParticipantStatus.Registered, fixture.Db.Participants.Single().Status);
    }

    [Fact]
    public async Task Answer_submission_cannot_reclaim_a_terminal_failed_turn()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turn = Assert.Single(fixture.Db.InterviewTurns);
        turn.AnswerTranscript = "Terminal answer";
        turn.AnsweredAt = StartTime;
        turn.ProcessingStatus = TurnProcessingStatus.Failed;
        turn.RetryCount = 3;

        var error = await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Terminal answer", turn.Id)));

        Assert.Equal("turn_processing_failed", error.Code);
        Assert.Equal(TurnProcessingStatus.Failed, turn.ProcessingStatus);
        Assert.Equal(3, turn.RetryCount);
        Assert.Null(turn.ProcessingToken);
        Assert.Equal(0, fixture.Analyzer.CallCount);
        Assert.Equal(0, fixture.Scorer.CallCount);
    }

    [Fact]
    public async Task Analyzer_failure_keeps_answer_and_identical_retry_recovers_processing()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        fixture.Analyzer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Durable answer", turnId)));

        var turn = Assert.Single(fixture.Db.InterviewTurns);
        Assert.Equal("Durable answer", turn.AnswerTranscript);
        Assert.NotNull(turn.AnsweredAt);
        Assert.Equal(TurnProcessingStatus.Retry, turn.ProcessingStatus);
        Assert.Empty(fixture.Db.CriterionScores);

        fixture.Clock.Set(StartTime.AddSeconds(5));
        var retry = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Durable answer", turnId));

        Assert.Equal(SubmitOutcome.Completed, retry.Outcome);
        Assert.Equal(TurnProcessingStatus.Completed, turn.ProcessingStatus);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Equal(2, fixture.Analyzer.CallCount);
        Assert.Equal(1, fixture.Scorer.CallCount);
    }

    [Fact]
    public async Task Manual_recovery_stays_committed_when_success_audit_fails_and_is_not_repeated()
    {
        await using var fixture = await CreateFixtureAsync(maximumTurnRetries: 0,
            auditLogWriter: new ThrowingAuditLogWriter());
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        fixture.Scorer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Recover after audit failure", turnId)));

        fixture.Configuration.AssignedLecturerId = Guid.NewGuid();
        var recoveryService = fixture.CreateService(new TestUser(
            fixture.Configuration.AssignedLecturerId.Value, Roles.Lecturer));

        var auditException = await Assert.ThrowsAsync<InvalidOperationException>(() => recoveryService.RecoverInterruptedTurnAsync(
            started.State.AttemptId, turnId));

        Assert.Equal(AttemptStatus.PendingReview, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal(TurnProcessingStatus.Completed, fixture.Db.InterviewTurns.Single().ProcessingStatus);
        Assert.Same(auditException, fixture.Logger.Exception);
        Assert.Equal(AuditLogActions.ManualRecoverySucceeded, fixture.Logger.Properties["AuditAction"]);
        Assert.Equal(started.State.AttemptId, fixture.Logger.Properties["AttemptId"]);
        Assert.Equal(turnId, fixture.Logger.Properties["TurnId"]);
        Assert.Equal(fixture.Configuration.AssignedLecturerId, fixture.Logger.Properties["ActorId"]);
        var analyzerCalls = fixture.Analyzer.CallCount;
        var scorerCalls = fixture.Scorer.CallCount;
        var speechToTextCalls = fixture.SpeechToText.CallCount;

        await Assert.ThrowsAsync<ConflictException>(() => recoveryService.RecoverInterruptedTurnAsync(
            started.State.AttemptId, turnId));

        Assert.Equal(analyzerCalls, fixture.Analyzer.CallCount);
        Assert.Equal(scorerCalls, fixture.Scorer.CallCount);
        Assert.Equal(speechToTextCalls, fixture.SpeechToText.CallCount);
    }

    [Fact]
    public async Task Manual_recovery_reprocesses_a_persisted_text_answer_after_session_closure()
    {
        await using var fixture = await CreateFixtureAsync(maximumTurnRetries: 0);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        fixture.Scorer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Persisted text answer", turnId)));

        fixture.Clock.Set(fixture.Session.EndTime.AddTicks(1));
        var lecturerId = Guid.NewGuid();
        fixture.Configuration.AssignedLecturerId = lecturerId;
        var recoveryService = fixture.CreateService(new TestUser(lecturerId, Roles.Lecturer));

        var recovered = await recoveryService.RecoverInterruptedTurnAsync(started.State.AttemptId, turnId);

        Assert.Equal(SubmitOutcome.Completed, recovered.Outcome);
        Assert.Equal(AttemptStatus.PendingReview, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal(TurnProcessingStatus.Completed, fixture.Db.InterviewTurns.Single().ProcessingStatus);
        Assert.Equal(2, fixture.Scorer.CallCount);
    }

    [Fact]
    public async Task Manual_recovery_rejects_ai_evaluation_without_a_persisted_transcript()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turn = fixture.Db.InterviewTurns.Single();
        turn.AnsweredAt = StartTime;
        turn.ProcessingStatus = TurnProcessingStatus.Failed;
        fixture.Db.InterviewAttempts.Single().Interrupt(StartTime.AddMinutes(1));

        var lecturerId = Guid.NewGuid();
        fixture.Configuration.AssignedLecturerId = lecturerId;
        var recoveryService = fixture.CreateService(new TestUser(lecturerId, Roles.Lecturer));

        var error = await Assert.ThrowsAsync<ConflictException>(() => recoveryService.RecoverInterruptedTurnAsync(
            started.State.AttemptId, turn.Id));

        Assert.Equal("transcript_not_persisted", error.Code);
        Assert.Equal(TurnProcessingStatus.Failed, turn.ProcessingStatus);
        Assert.Equal(0, fixture.Analyzer.CallCount);
        Assert.Equal(0, fixture.Scorer.CallCount);
    }

    [Fact]
    public async Task Explicitly_empty_answer_is_distinct_from_unanswered_timeout()
    {
        await using var submitted = await CreateFixtureAsync();
        var started = await submitted.Service.StartAttemptAsync(new StartAttemptRequest(submitted.Session.Id));
        await submitted.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("", started.State.CurrentTurn!.TurnId));
        Assert.Equal(string.Empty, submitted.Db.InterviewTurns.Single().AnswerTranscript);
        Assert.NotNull(submitted.Db.InterviewTurns.Single().AnsweredAt);
        Assert.False(submitted.Db.InterviewTurns.Single().IsTimedOut);

        await using var timedOut = await CreateFixtureAsync(timeLimitSeconds: 1);
        var timedOutStart = await timedOut.Service.StartAttemptAsync(new StartAttemptRequest(timedOut.Session.Id));
        timedOut.Clock.Set(StartTime.AddSeconds(2));
        await timedOut.Service.ExpireOverdueAttemptsAsync();
        var expiredTurn = Assert.Single(timedOut.Db.InterviewTurns);
        Assert.Null(expiredTurn.AnswerTranscript);
        Assert.Null(expiredTurn.AnsweredAt);
        Assert.True(expiredTurn.IsTimedOut);
        Assert.Null((await timedOut.Service.GetAttemptStateAsync(timedOutStart.State.AttemptId)).CurrentTurn);
    }

    [Fact]
    public async Task Same_turn_id_with_different_answer_is_rejected_without_mutation()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("First answer", turnId));

        var error = await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Different answer", turnId)));

        Assert.Equal("turn_submission_conflict", error.Code);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Single(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Next_turn_deadline_starts_after_ai_processing_opens_the_turn()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 10, questionCount: 2);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(1));
        fixture.Analyzer.PauseNextCall();

        var submission = fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Answer", started.State.CurrentTurn!.TurnId));
        await fixture.Analyzer.WaitUntilPausedAsync();
        fixture.Clock.Set(StartTime.AddSeconds(40));
        fixture.Analyzer.Release();
        var result = await submission;

        Assert.Equal(StartTime.AddSeconds(40), result.Attempt.CurrentTurn!.AskedAt);
        Assert.Equal(StartTime.AddSeconds(50), result.Attempt.CurrentTurn.DeadlineAt);
    }

    [Fact]
    public async Task Slow_ai_processing_does_not_open_a_turn_after_the_exam_session_closes()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 10, questionCount: 2);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(1));
        fixture.Analyzer.PauseNextCall();

        var submission = fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Answer", started.State.CurrentTurn!.TurnId));
        await fixture.Analyzer.WaitUntilPausedAsync();
        fixture.Clock.Set(fixture.Session.EndTime.AddTicks(1));
        fixture.Analyzer.Release();
        var result = await submission;

        Assert.Equal(AttemptStatus.PendingReview.ToString(), result.Attempt.Status);
        Assert.Null(result.Attempt.CurrentTurn);
        Assert.Contains(fixture.Events.Events, interviewEvent => interviewEvent.Type == "attempt.expired");
        Assert.Single(fixture.Db.InterviewTurns);
    }

    [Fact]
    public async Task Concurrent_same_turn_retries_share_one_analysis_and_one_score()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        fixture.Analyzer.PauseNextCall();

        var originalTask = fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Same answer", turnId));
        await fixture.Analyzer.WaitUntilPausedAsync();
        var retry = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Same answer", turnId));
        fixture.Analyzer.Release();
        var original = await originalTask;

        Assert.False(original.Duplicate);
        Assert.True(retry.Duplicate);
        Assert.Equal(1, fixture.Analyzer.CallCount);
        Assert.Equal(1, fixture.Scorer.CallCount);
        Assert.Single(fixture.Db.QuestionResponses);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Single(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Submission_at_deadline_is_accepted_and_late_submission_is_rejected()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 10);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(10));

        var result = await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("At the boundary", started.State.CurrentTurn!.TurnId));

        Assert.Equal(SubmitOutcome.Completed, result.Outcome);

        await using var lateFixture = await CreateFixtureAsync(timeLimitSeconds: 10);
        var lateStart = await lateFixture.Service.StartAttemptAsync(new StartAttemptRequest(lateFixture.Session.Id));
        lateFixture.Clock.Set(StartTime.AddSeconds(10).AddTicks(1));

        var error = await Assert.ThrowsAsync<ConflictException>(() => lateFixture.Service.SubmitAnswerAsync(
            lateStart.State.AttemptId, new SubmitAnswerRequest("Too late", lateStart.State.CurrentTurn!.TurnId)));

        Assert.Equal("answer_deadline_passed", error.Code);
        Assert.Null(lateFixture.Db.InterviewTurns.Single().AnswerTranscript);
        Assert.Null(lateFixture.Db.InterviewTurns.Single().AnsweredAt);
        Assert.Empty(lateFixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Starting_again_resumes_the_same_eligible_attempt_and_turn()
    {
        await using var fixture = await CreateFixtureAsync();
        var first = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));

        var resumed = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));

        Assert.True(first.Created);
        Assert.False(resumed.Created);
        Assert.Equal(first.State.AttemptId, resumed.State.AttemptId);
        Assert.Equal(first.State.CurrentTurn!.TurnId, resumed.State.CurrentTurn!.TurnId);
        Assert.Single(fixture.Db.InterviewAttempts);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Equal("attempt.resumed", fixture.Events.Events[^1].Type);
    }

    [Fact]
    public async Task Retake_obeys_configuration_and_creates_a_distinct_attempt()
    {
        await using var blocked = await CreateFixtureAsync(allowRetake: false);
        var first = await blocked.Service.StartAttemptAsync(new StartAttemptRequest(blocked.Session.Id));
        await blocked.Service.SubmitAnswerAsync(first.State.AttemptId,
            new SubmitAnswerRequest("First attempt", first.State.CurrentTurn!.TurnId));
        await Assert.ThrowsAsync<ConflictException>(() => blocked.Service.StartAttemptAsync(
            new StartAttemptRequest(blocked.Session.Id)));
        Assert.Single(blocked.Db.InterviewAttempts);

        await using var allowed = await CreateFixtureAsync(allowRetake: true);
        var firstAllowed = await allowed.Service.StartAttemptAsync(new StartAttemptRequest(allowed.Session.Id));
        await allowed.Service.SubmitAnswerAsync(firstAllowed.State.AttemptId,
            new SubmitAnswerRequest("First attempt", firstAllowed.State.CurrentTurn!.TurnId));

        var retake = await allowed.Service.StartAttemptAsync(new StartAttemptRequest(allowed.Session.Id));

        Assert.True(retake.Created);
        Assert.NotEqual(firstAllowed.State.AttemptId, retake.State.AttemptId);
        Assert.Equal(2, allowed.Db.InterviewAttempts.Count());
        Assert.Equal(2, allowed.Db.InterviewTurns.Count());
    }

    [Fact]
    public async Task Interrupted_retake_does_not_regress_a_participant_with_a_completed_attempt()
    {
        await using var fixture = await CreateFixtureAsync(allowRetake: true, maximumTurnRetries: 0);
        var first = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        await fixture.Service.SubmitAnswerAsync(first.State.AttemptId,
            new SubmitAnswerRequest("Completed attempt", first.State.CurrentTurn!.TurnId));
        Assert.Equal(ParticipantStatus.Completed, fixture.Db.Participants.Single().Status);

        var second = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Scorer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SubmitAnswerAsync(
            second.State.AttemptId, new SubmitAnswerRequest("Interrupted retake", second.State.CurrentTurn!.TurnId)));

        Assert.Equal(AttemptStatus.PendingReview, fixture.Db.InterviewAttempts.Single(a => a.Id == first.State.AttemptId).Status);
        Assert.Equal(AttemptStatus.Interrupted, fixture.Db.InterviewAttempts.Single(a => a.Id == second.State.AttemptId).Status);
        Assert.Equal(ParticipantStatus.Completed, fixture.Db.Participants.Single().Status);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Null(fixture.Db.QuestionResponses.Single(response => response.AttemptId == second.State.AttemptId).AiSuggestedScore);
        Assert.Empty(fixture.Db.ScoreReviews);
    }

    [Fact]
    public async Task Expiry_worker_processes_an_expired_turn_once()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 10);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(11));

        var processed = await fixture.Service.ExpireOverdueAttemptsAsync();
        var repeatedSweep = await fixture.Service.ExpireOverdueAttemptsAsync();

        Assert.Equal(1, processed);
        Assert.Equal(0, repeatedSweep);
        Assert.Equal(AttemptStatus.PendingReview, fixture.Db.InterviewAttempts.Single().Status);
        Assert.False(fixture.Db.InterviewTurns.Single().AnsweredAt.HasValue);
        Assert.True(fixture.Db.InterviewTurns.Single().IsTimedOut);
        Assert.Null(fixture.Db.InterviewTurns.Single().AnswerTranscript);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Equal(1, fixture.Scorer.CallCount);
        Assert.Null((await fixture.Service.GetAttemptStateAsync(started.State.AttemptId)).CurrentTurn);
        Assert.Contains(fixture.Events.Events, interviewEvent => interviewEvent.Type == "turn.deadline_reached");
        Assert.Contains(fixture.Events.Events, interviewEvent => interviewEvent.Type == "attempt.completed");
        var lateAnswer = await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Late retry", started.State.CurrentTurn!.TurnId)));
        Assert.Equal("answer_deadline_passed", lateAnswer.Code);
    }

    [Fact]
    public async Task Answer_and_expiry_sweep_race_keeps_one_consistent_turn_result()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 10);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(9));
        fixture.Analyzer.PauseNextCall();

        var submission = fixture.Service.SubmitAnswerAsync(
            started.State.AttemptId, new SubmitAnswerRequest("Accepted before deadline", started.State.CurrentTurn!.TurnId));
        await fixture.Analyzer.WaitUntilPausedAsync();
        fixture.Clock.Set(StartTime.AddSeconds(11));
        var expired = await fixture.Service.ExpireOverdueAttemptsAsync();
        fixture.Analyzer.Release();
        var result = await submission;

        Assert.Equal(0, expired);
        Assert.False(result.Duplicate);
        Assert.Equal(AttemptStatus.PendingReview, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Single(fixture.Db.InterviewTurns);
        Assert.Single(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Legacy_score_review_can_finalize_an_attempt_after_turn_processing()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        await fixture.Service.SubmitAnswerAsync(started.State.AttemptId,
            new SubmitAnswerRequest("Answer", started.State.CurrentTurn!.TurnId));
        var reviewerId = Guid.NewGuid();
        var review = new InterviewReviewService(fixture.Db, fixture.Configuration,
            new InterviewResultsApi(fixture.Db), fixture.Events, new TestUser(reviewerId, Roles.Lecturer), fixture.Clock);

        var approved = await review.SubmitScoreReviewAsync(started.State.AttemptId,
            new SubmitScoreReviewRequest(7m, "Confirmed"));

        Assert.Equal(ReviewStatus.Approved, approved.Status);
        Assert.Equal(AttemptStatus.Finalized, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal("review.finalized", fixture.Events.Events[^1].Type);
    }

    [Fact]
    public async Task Subscription_authorization_is_scoped_to_student_attempts_and_assigned_lecturers()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));

        Assert.Equal(fixture.Session.Id,
            await fixture.Service.AuthorizeAttemptSubscriptionAsync(started.State.AttemptId));
        await Assert.ThrowsAsync<NotFoundException>(() => fixture.CreateService(
                new TestUser(Guid.NewGuid(), Roles.Student))
            .AuthorizeAttemptSubscriptionAsync(started.State.AttemptId));
        Assert.Equal(fixture.Session.Id,
            await fixture.CreateService(new TestUser(Guid.NewGuid(), Roles.Lecturer))
                .AuthorizeAttemptSubscriptionAsync(started.State.AttemptId));
        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Service.AuthorizeSessionSubscriptionAsync(fixture.Session.Id));
    }

    [Fact]
    public async Task Audio_chunk_submission_requires_the_attempt_owner_and_current_turn()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;

        await Assert.ThrowsAsync<NotFoundException>(() => fixture.CreateAudioService(
                new TestUser(Guid.NewGuid(), Roles.Student))
            .SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1], "audio/webm"));
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.CreateAudioService(
                new TestUser(fixture.Db.Participants.Single().StudentId, Roles.Lecturer))
            .SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1], "audio/webm"));
        await Assert.ThrowsAsync<ConflictException>(() => fixture.CreateAudioService(
                new TestUser(fixture.Db.Participants.Single().StudentId, Roles.Student))
            .SendAudioChunkAsync(started.State.AttemptId, Guid.NewGuid(), 0, [1], "audio/webm"));

        Assert.Equal(0, fixture.AudioStore.AppendCount);
    }

    [Fact]
    public async Task Audio_chunks_are_retry_safe_and_reject_out_of_order_or_conflicting_sequences()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        var turnId = started.State.CurrentTurn!.TurnId;

        var first = await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2], "audio/webm");
        var retry = await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2], "audio/webm");

        Assert.False(first.Duplicate);
        Assert.True(retry.Duplicate);
        Assert.Equal(1, retry.NextSequence);
        Assert.Equal(2, retry.BytesReceived);
        Assert.Equal("audio_chunk_out_of_order", (await Assert.ThrowsAsync<ConflictException>(() =>
            audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 2, [3], "audio/webm"))).Code);
        Assert.Equal("audio_chunk_duplicate_conflict", (await Assert.ThrowsAsync<ConflictException>(() =>
            audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [9], "audio/webm"))).Code);
    }

    [Fact]
    public async Task Audio_checkpoint_allows_upload_to_resume_after_reconnect()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var student = new TestUser(fixture.Db.Participants.Single().StudentId, Roles.Student);
        var initialConnection = fixture.CreateAudioService(student);
        var turnId = started.State.CurrentTurn!.TurnId;
        await initialConnection.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1], "audio/webm");

        var reconnected = fixture.CreateAudioService(student);
        var checkpoint = await reconnected.GetAudioCheckpointAsync(started.State.AttemptId, turnId);
        var next = await reconnected.SendAudioChunkAsync(started.State.AttemptId, turnId,
            checkpoint!.NextSequence, [2], "audio/webm");

        Assert.Equal(1, checkpoint.NextSequence);
        Assert.Equal(2, next.NextSequence);
        Assert.Equal(2, next.BytesReceived);
    }

    [Fact]
    public async Task Audio_completion_transcribes_then_uses_durable_answer_submission_and_state_version()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        var turnId = started.State.CurrentTurn!.TurnId;
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");

        var result = await audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        var retry = await audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);

        Assert.Equal(SubmitOutcome.Completed, result.Outcome);
        Assert.True(retry.Duplicate);
        Assert.Equal(result.Outcome, retry.Outcome);
        Assert.Equal("transcribed answer", fixture.Db.InterviewTurns.Single().AnswerTranscript);
        Assert.Equal(1, fixture.SpeechToText.CallCount);
        Assert.Equal(fixture.Events.Events[^1].StateVersion, result.Attempt.StateVersion);
        Assert.Equal(TurnProcessingStatus.Completed, fixture.Db.InterviewTurns.Single().ProcessingStatus);
    }

    [Fact]
    public async Task Audio_completion_publishes_attempt_scoped_transcript_only_after_answer_persistence()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student), new InMemoryAudioChunkStore());
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");

        await audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        await audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);

        var transcriptEvent = Assert.Single(fixture.Events.TranscriptUpdates);
        Assert.Equal(started.State.AttemptId, transcriptEvent.AttemptId);
        Assert.Equal(turnId, transcriptEvent.TurnId);
        Assert.Equal("transcribed answer", transcriptEvent.Transcript);
        Assert.Equal("transcribed answer", fixture.Db.InterviewTurns.Single().AnswerTranscript);
    }

    [Fact]
    public async Task Transcript_updated_is_published_once_after_audio_retry_eventual_success()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student), new InMemoryAudioChunkStore());
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");
        fixture.Scorer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId));

        Assert.Empty(fixture.Events.TranscriptUpdates);
        var turn = fixture.Db.InterviewTurns.Single();
        Assert.Equal(TurnProcessingStatus.Retry, turn.ProcessingStatus);
        Assert.NotNull(turn.AnswerTranscript);
        fixture.Clock.Set(turn.NextRetryAt!.Value);

        Assert.Equal(1, await fixture.Service.ExpireOverdueAttemptsAsync());

        Assert.Single(fixture.Events.TranscriptUpdates);
        Assert.Equal(started.State.AttemptId, fixture.Events.TranscriptUpdates[0].AttemptId);
        Assert.Single(fixture.Db.CriterionScores);
        await audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        Assert.Single(fixture.Events.TranscriptUpdates);
    }

    [Fact]
    public async Task Audio_failure_before_successful_submission_does_not_publish_transcript_updated()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student), new InMemoryAudioChunkStore());
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");
        fixture.Scorer.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId));

        Assert.Empty(fixture.Events.TranscriptUpdates);
    }

    [Fact]
    public async Task Failed_transcription_does_not_publish_transcript_updated()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student), new InMemoryAudioChunkStore());
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");
        fixture.SpeechToText.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId));

        Assert.Empty(fixture.Events.TranscriptUpdates);
    }

    [Fact]
    public async Task Audio_chunks_are_rejected_after_the_turn_deadline()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Clock.Set(StartTime.AddSeconds(2));

        await Assert.ThrowsAsync<ConflictException>(() => fixture.CreateAudioService(new TestUser(
                fixture.Db.Participants.Single().StudentId, Roles.Student))
            .SendAudioChunkAsync(started.State.AttemptId, started.State.CurrentTurn!.TurnId,
                0, [1], "audio/webm"));

        Assert.Equal(0, fixture.AudioStore.AppendCount);
        Assert.Equal(0, fixture.SpeechToText.CallCount);
    }

    [Fact]
    public async Task Simultaneous_audio_completion_requests_commit_one_processing_lease()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        var turnId = started.State.CurrentTurn!.TurnId;
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");
        fixture.SpeechToText.PauseNextCall();

        var firstCompletion = audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        await fixture.SpeechToText.WaitUntilPausedAsync();
        var secondCompletion = await audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        fixture.SpeechToText.Release();
        var firstResult = await firstCompletion;

        Assert.False(firstResult.Duplicate);
        Assert.True(secondCompletion.Duplicate);
        Assert.Equal(1, fixture.SpeechToText.CallCount);
        Assert.Single(fixture.Db.CriterionScores);
        Assert.Equal(TurnProcessingStatus.Completed, fixture.Db.InterviewTurns.Single().ProcessingStatus);
    }

    [Fact]
    public async Task Audio_completion_does_not_start_transcription_after_the_deadline()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        var turnId = started.State.CurrentTurn!.TurnId;
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1], "audio/webm");
        fixture.Clock.Set(StartTime.AddSeconds(2));

        await Assert.ThrowsAsync<ConflictException>(() =>
            audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId));

        Assert.Equal(0, fixture.AudioStore.CompleteCount);
        Assert.Equal(0, fixture.SpeechToText.CallCount);
    }

    [Fact]
    public async Task Unconfigured_speech_to_text_reports_unavailable_instead_of_fabricating_a_transcript()
    {
        var exception = await Assert.ThrowsAsync<AppException>(() =>
            new UnavailableSpeechToTextService().TranscribeAsync(
                new MemoryStream([1]), "audio/webm", "Vietnamese"));

        Assert.Equal(503, exception.StatusCode);
        Assert.Equal("speech_to_text_unavailable", exception.Code);
    }

    [Fact]
    public async Task Get_attempt_state_recovers_current_authoritative_state_without_event_replay()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));

        var recovered = await fixture.Service.GetAttemptStateAsync(started.State.AttemptId);

        Assert.Equal(started.State.AttemptId, recovered.AttemptId);
        Assert.Equal(started.State.CurrentTurn?.TurnId, recovered.CurrentTurn?.TurnId);
        Assert.DoesNotContain("score", string.Join(',', typeof(AttemptStateDto).GetProperties().Select(property => property.Name)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Fixture> CreateFixtureAsync(
        int timeLimitSeconds = 60,
        bool allowRetake = false,
        int questionCount = 1,
        int maxFollowUps = 0,
        int maximumTurnRetries = 3,
        int initialRetryDelaySeconds = 5,
        int maximumRetryDelaySeconds = 300,
        IAuditLogWriter? auditLogWriter = null)
    {
        var now = StartTime;
        var clock = new TestClock(now);
        var criterionId = Guid.NewGuid();
        var questions = Enumerable.Range(1, questionCount)
            .Select(orderNo => new QuestionInfo(Guid.NewGuid(), orderNo, $"Question {orderNo}",
                [new RubricCriterionInfo(criterionId, 1, "Accuracy", 10m)], 10m))
            .ToList();
        var session = new ExamSessionInfo(
            Guid.NewGuid(), Guid.NewGuid(), "TEST", "Test course", "Test exam",
            StartTime.AddHours(-1), StartTime.AddHours(1), timeLimitSeconds, maxFollowUps,
            ExamSessionStatus.Published,
            questions);
        var studentId = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();
        var db = new InterviewDbContext(new DbContextOptionsBuilder<InterviewDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options);
        db.Participants.Add(new Participant
        {
            Id = Guid.NewGuid(),
            ExamSessionId = session.Id,
            StudentId = studentId,
            JoinedAt = StartTime,
            Status = ParticipantStatus.Registered
        });
        await db.SaveChangesAsync();

        var analyzer = new TestAnalyzer();
        var scorer = new TestScorer(criterionId);
        var events = new TestEventPublisher(db);
        var configuration = new TestExamConfiguration(session);
        var audioStore = new TestAudioChunkStore();
        var speechToText = new TestSpeechToText();
        var logger = new TestLogger();
        var interviewOptions = new InterviewOptions
        {
            AllowRetake = allowRetake,
            TimeLimitGraceSeconds = 0,
            MaximumTurnRetries = maximumTurnRetries,
            InitialTurnRetryDelaySeconds = initialRetryDelaySeconds,
            MaximumTurnRetryDelaySeconds = maximumRetryDelaySeconds
        };
        var service = new InterviewService(db, configuration, analyzer, scorer,
            audioStore, speechToText, events, auditLogWriter ?? new TestAuditLogWriter(), new TestUser(studentId, Roles.Student), clock,
            Options.Create(interviewOptions),
            logger);
        return new Fixture(db, service, clock, analyzer, scorer, configuration, session, events,
            audioStore, speechToText, databaseName, auditLogWriter ?? new TestAuditLogWriter(), logger);
    }

    private sealed class Fixture(
        InterviewDbContext db,
        InterviewService service,
        TestClock clock,
        TestAnalyzer analyzer,
        TestScorer scorer,
        TestExamConfiguration configuration,
        ExamSessionInfo session,
        TestEventPublisher events,
        TestAudioChunkStore audioStore,
        TestSpeechToText speechToText,
        string databaseName,
        IAuditLogWriter auditLogWriter,
        TestLogger logger) : IAsyncDisposable
    {
        public InterviewDbContext Db { get; } = db;
        public InterviewService Service { get; } = service;
        public TestClock Clock { get; } = clock;
        public TestAnalyzer Analyzer { get; } = analyzer;
        public TestScorer Scorer { get; } = scorer;
        public TestExamConfiguration Configuration { get; } = configuration;
        public ExamSessionInfo Session { get; } = session;
        public TestEventPublisher Events { get; } = events;
        public TestAudioChunkStore AudioStore { get; } = audioStore;
        public TestSpeechToText SpeechToText { get; } = speechToText;
        public IAuditLogWriter AuditLogWriter { get; } = auditLogWriter;
        public TestLogger Logger { get; } = logger;
        public InterviewDbContext CreateIndependentDb() => new(new DbContextOptionsBuilder<InterviewDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options);
        public InterviewService CreateIndependentService(InterviewDbContext db) => new(db, Configuration,
            new TestAnalyzer(), Scorer, AudioStore, SpeechToText, new TestEventPublisher(db),
            AuditLogWriter, new TestUser(Db.Participants.Single().StudentId, Roles.Student), Clock,
            Options.Create(new InterviewOptions { TimeLimitGraceSeconds = 0 }),
            NullLogger<InterviewService>.Instance);
        public InterviewService CreateService(TestUser user) => new(Db, Configuration, Analyzer, Scorer,
            AudioStore, SpeechToText, Events, AuditLogWriter, user, Clock, Options.Create(new InterviewOptions { TimeLimitGraceSeconds = 0 }),
            Logger);
        public InterviewAudioSubmissionService CreateAudioService(TestUser user, IAudioChunkStore? audioStore = null)
        {
            var store = audioStore ?? AudioStore;
            var workflowService = ReferenceEquals(store, AudioStore)
                ? Service
                : new InterviewService(Db, Configuration, Analyzer, Scorer,
                    store, SpeechToText, Events, new TestAuditLogWriter(), user, Clock,
                    Options.Create(new InterviewOptions { TimeLimitGraceSeconds = 0 }),
                    NullLogger<InterviewService>.Instance);
            return new InterviewAudioSubmissionService(Db, Configuration,
                user, store, workflowService, Clock,
                Options.Create(new InterviewOptions { TimeLimitGraceSeconds = 0 }));
        }

    private sealed class ThrowingAuditLogWriter : IAuditLogWriter
    {
        public Task AppendAsync(AuditLogEntry entry, CancellationToken ct = default) =>
            throw new InvalidOperationException("Audit append failure.");
    }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class TestAudioChunkStore : IAudioChunkStore
    {
        private readonly Dictionary<(Guid AttemptId, Guid TurnId), AudioUpload> _uploads = [];
        public int AppendCount { get; private set; }
        public int CompleteCount { get; private set; }
        public bool FailNextCompletionAsEmpty { get; set; }
        private TaskCompletionSource? _completionStarted;
        private TaskCompletionSource? _completionRelease;

        public void PauseNextCompletion()
        {
            _completionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _completionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilCompletionStartedAsync()
        {
            if (_completionStarted is null)
                throw new InvalidOperationException("Audio completion is not paused.");
            return _completionStarted.Task;
        }

        public void ReleaseCompletion()
        {
            _completionRelease?.TrySetResult();
            _completionRelease = null;
        }

        public void RemoveUpload(Guid attemptId, Guid turnId) =>
            _uploads.Remove((attemptId, turnId));

        public Task<AudioChunkAppendResult> AppendAsync(
            Guid attemptId, Guid turnId, int sequence, byte[] chunk, string contentType, CancellationToken ct)
        {
            AppendCount++;
            var key = (attemptId, turnId);
            if (!_uploads.TryGetValue(key, out var upload))
                _uploads[key] = upload = new AudioUpload(contentType);

            AudioChunkAppendStatus status;
            if (upload.IsCompleted)
                status = AudioChunkAppendStatus.UploadCompleted;
            else if (!string.Equals(upload.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
                status = AudioChunkAppendStatus.ContentTypeConflict;
            else if (sequence < upload.Chunks.Count)
                status = upload.Chunks[sequence].SequenceEqual(chunk)
                    ? AudioChunkAppendStatus.Duplicate
                    : AudioChunkAppendStatus.ConflictingDuplicate;
            else if (sequence > upload.Chunks.Count)
                status = AudioChunkAppendStatus.OutOfOrder;
            else
            {
                upload.Chunks.Add(chunk.ToArray());
                status = AudioChunkAppendStatus.Appended;
            }

            return Task.FromResult(new AudioChunkAppendResult(status, upload.ToCheckpoint()));
        }

        public Task<AudioChunkCheckpoint?> GetCheckpointAsync(Guid attemptId, Guid turnId, CancellationToken ct) =>
            Task.FromResult(_uploads.TryGetValue((attemptId, turnId), out var upload)
                ? upload.ToCheckpoint()
                : null);

        public async Task<CompletedAudio> CompleteUploadAsync(Guid attemptId, Guid turnId, CancellationToken ct)
        {
            CompleteCount++;
            if (_completionRelease is { } release)
            {
                _completionStarted!.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            if (FailNextCompletionAsEmpty)
            {
                FailNextCompletionAsEmpty = false;
                throw new ConflictException("audio_upload_empty", "Upload at least one audio chunk before completing.");
            }
            if (!_uploads.TryGetValue((attemptId, turnId), out var upload) || upload.Chunks.Count == 0)
                throw new ConflictException("audio_upload_empty", "Upload at least one audio chunk before completing.");
            upload.IsCompleted = true;
            var bytes = upload.Chunks.SelectMany(chunk => chunk).ToArray();
            return new CompletedAudio(new MemoryStream(bytes, writable: false),
                upload.ContentType, upload.Transcript);
        }

        public Task<string> SaveTranscriptIfAbsentAsync(
            Guid attemptId, Guid turnId, string transcript, CancellationToken ct)
        {
            var upload = _uploads[(attemptId, turnId)];
            upload.Transcript ??= transcript;
            return Task.FromResult(upload.Transcript);
        }

        private sealed class AudioUpload(string contentType)
        {
            public string ContentType { get; } = contentType;
            public List<byte[]> Chunks { get; } = [];
            public bool IsCompleted { get; set; }
            public string? Transcript { get; set; }
            public AudioChunkCheckpoint ToCheckpoint() => new(Chunks.Count,
                Chunks.Sum(chunk => (long)chunk.Length), ContentType, IsCompleted, Transcript);
        }
    }

    private sealed class TestSpeechToText : ISpeechToTextService
    {
        public int CallCount { get; private set; }
        public bool FailNext { get; set; }
        private TaskCompletionSource? _started;
        private TaskCompletionSource? _release;

        public void PauseNextCall()
        {
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilPausedAsync()
        {
            if (_started is null)
                throw new InvalidOperationException("Speech-to-text is not paused.");
            return _started.Task;
        }

        public void Release()
        {
            _release?.TrySetResult();
            _release = null;
        }

        public async Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken ct = default)
        {
            CallCount++;
            if (_release is { } release)
            {
                _started!.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Speech-to-text failure.");
            }
            return "transcribed answer";
        }
    }

    private sealed class TestEventPublisher(InterviewDbContext db) : IInterviewEventPublisher
    {
        public List<InterviewAttemptEvent> Events { get; } = [];
        public List<TranscriptUpdatedEvent> TranscriptUpdates { get; } = [];

        public Task PublishTranscriptUpdatedAsync(TranscriptUpdatedEvent transcriptEvent, CancellationToken ct = default)
        {
            Assert.Contains(db.InterviewTurns, turn => turn.Id == transcriptEvent.TurnId
                && turn.AnswerTranscript == transcriptEvent.Transcript);
            TranscriptUpdates.Add(transcriptEvent);
            return Task.CompletedTask;
        }

        public Task PublishAsync(InterviewAttemptEvent interviewEvent, CancellationToken ct = default)
        {
            var attempt = Assert.Single(db.InterviewAttempts.Where(item => item.Id == interviewEvent.AttemptId));
            if (interviewEvent.Type == "turn.opened")
                Assert.NotEmpty(db.InterviewTurns);
            if (interviewEvent.Type == "answer.accepted")
                Assert.Contains(db.InterviewTurns, turn => turn.AnsweredAt.HasValue);
            if (interviewEvent.Type is "turn.deadline_reached" or "turn.closed")
                Assert.Contains(db.InterviewTurns, turn => turn.IsTimedOut && turn.AnsweredAt is null);
            if (interviewEvent.Type == "review.finalized")
                Assert.Equal(AttemptStatus.Finalized, attempt.Status);
            Events.Add(interviewEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class TestClock(DateTime now) : TimeProvider
    {
        private DateTime _now = now;
        public override DateTimeOffset GetUtcNow() => new(_now);
        public void Set(DateTime value) => _now = value;
    }

    private sealed class TestUser(Guid id, string role) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => role;
        public bool IsInRole(string requestedRole) => role == requestedRole;
    }

    private sealed class TestAnalyzer : IAnswerAnalyzer
    {
        private TaskCompletionSource? _started;
        private TaskCompletionSource? _release;
        public int CallCount { get; private set; }
        public bool FailNext { get; set; }
        public bool NeedsFollowUp { get; set; }

        public async Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default)
        {
            CallCount++;
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Analyzer failure.");
            }
            if (_release is { } release)
            {
                _started!.TrySetResult();
                await release.Task.WaitAsync(ct);
                _release = null;
            }
            return new AnswerAnalysis(true, [], NeedsFollowUp, NeedsFollowUp ? "Follow-up question" : null);
        }

        public void PauseNextCall()
        {
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilPausedAsync() => _started!.Task;
        public void Release() => _release!.TrySetResult();
    }

    private sealed class TestScorer(Guid criterionId) : IRubricScorer
    {
        public int CallCount { get; private set; }
        public bool FailNext { get; set; }
        public RubricScore? ResultOverride { get; set; }
        public string? RawJsonOverride { get; set; }

        public Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default)
        {
            CallCount++;
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Scorer failure.");
            }
            if (RawJsonOverride is not null)
                return Task.FromResult(JsonSerializer.Deserialize<RubricScore>(RawJsonOverride,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
            return Task.FromResult(ResultOverride
                ?? new RubricScore([new CriterionPoints(criterionId, 8m, "Good")], "Good answer."));
        }
    }

    private sealed class TestExamConfiguration(ExamSessionInfo session) : IExamConfigurationApi
    {
        public Guid? AssignedLecturerId { get; set; }

        public Task<ExamSessionInfo?> GetSessionAsync(Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult<ExamSessionInfo?>(examSessionId == session.Id ? session : null);

        public Task<IReadOnlyList<ExamSessionInfo>> GetSessionsAsync(
            IReadOnlyCollection<Guid> examSessionIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExamSessionInfo>>(examSessionIds.Contains(session.Id) ? [session] : []);

        public Task<bool> IsLecturerOfSessionAsync(Guid lecturerId, Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult(examSessionId == session.Id
                && (AssignedLecturerId is null || AssignedLecturerId == lecturerId));

        public Task<IReadOnlyList<UserBasicInfo>> GetUsersAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserBasicInfo>>([]);

        public Task<UserAccessInfo?> GetUserAccessAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<UserAccessInfo?>(new UserAccessInfo(Roles.Lecturer, true));
    }

    private sealed class TestAuditLogWriter : IAuditLogWriter
    {
        public Task AppendAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingAuditLogWriter : IAuditLogWriter
    {
        public Task AppendAsync(AuditLogEntry entry, CancellationToken ct = default) =>
            throw new InvalidOperationException("Audit append failure.");
    }

    private sealed class TestLogger : ILogger<InterviewService>
    {
        public Exception? Exception { get; private set; }
        public IReadOnlyDictionary<string, object?> Properties { get; private set; } =
            new Dictionary<string, object?>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            Exception = exception;
            Properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.Where(item => item.Key != "{OriginalFormat}")
                    .ToDictionary(item => item.Key, item => item.Value)
                : new Dictionary<string, object?>();
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public async Task Audio_completion_accepted_before_deadline_survives_stt_finishing_after_deadline_and_worker_poll()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");

        fixture.SpeechToText.PauseNextCall();
        var completion = audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        await fixture.SpeechToText.WaitUntilPausedAsync();

        fixture.Clock.Set(StartTime.AddSeconds(2));
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
        var duringProcessing = fixture.Db.InterviewTurns.Single();
        Assert.False(duringProcessing.IsTimedOut);
        Assert.Equal(TurnProcessingStatus.Processing, duringProcessing.ProcessingStatus);
        Assert.NotNull(duringProcessing.AudioAcceptedAt);
        Assert.Null(duringProcessing.AnsweredAt);

        fixture.SpeechToText.Release();
        var result = await completion;

        Assert.Equal(SubmitOutcome.Completed, result.Outcome);
        var turn = fixture.Db.InterviewTurns.Single();
        Assert.NotNull(turn.AudioAcceptedAt);
        Assert.Equal(turn.AudioAcceptedAt, turn.AnsweredAt);
        Assert.Equal("transcribed answer", turn.AnswerTranscript);
        Assert.Equal(TurnProcessingStatus.Completed, fixture.Db.InterviewTurns.Single().ProcessingStatus);
    }

    [Fact]
    public async Task Audio_completion_received_before_deadline_is_not_timed_out_while_upload_finalization_is_in_flight()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");
        fixture.AudioStore.PauseNextCompletion();

        var completion = audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        await fixture.AudioStore.WaitUntilCompletionStartedAsync();

        fixture.Clock.Set(StartTime.AddSeconds(2));
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
        var inFlight = fixture.Db.InterviewTurns.Single();
        Assert.Equal(TurnProcessingStatus.Processing, inFlight.ProcessingStatus);
        Assert.False(inFlight.IsTimedOut);
        Assert.Null(inFlight.AudioAcceptedAt);

        fixture.AudioStore.ReleaseCompletion();
        var result = await completion;

        Assert.Equal(SubmitOutcome.Completed, result.Outcome);
        var committed = fixture.Db.InterviewTurns.Single();
        Assert.Equal(StartTime, committed.AudioAcceptedAt);
        Assert.False(committed.IsTimedOut);
        Assert.Equal(TurnProcessingStatus.Completed, committed.ProcessingStatus);
    }

    [Fact]
    public async Task Audio_completion_validation_failure_releases_the_claim_and_allows_deadline_recovery()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");
        fixture.AudioStore.FailNextCompletionAsEmpty = true;

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId));

        Assert.Equal("audio_upload_empty", error.Code);
        var released = fixture.Db.InterviewTurns.Single();
        Assert.Equal(TurnProcessingStatus.AwaitingAnswer, released.ProcessingStatus);
        Assert.Null(released.ProcessingToken);
        Assert.Null(released.AudioAcceptedAt);

        fixture.Clock.Set(StartTime.AddSeconds(2));
        Assert.Equal(1, await fixture.Service.ExpireOverdueAttemptsAsync());
        Assert.True(fixture.Db.InterviewTurns.Single().IsTimedOut);
    }

    [Fact]
    public async Task Simultaneous_audio_completion_requests_during_upload_validation_are_idempotent()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");
        fixture.AudioStore.PauseNextCompletion();

        var first = audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        await fixture.AudioStore.WaitUntilCompletionStartedAsync();
        var second = await audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);

        fixture.AudioStore.ReleaseCompletion();
        var primary = await first;

        Assert.False(primary.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(2, fixture.AudioStore.CompleteCount);
        Assert.Equal(TurnProcessingStatus.Completed, fixture.Db.InterviewTurns.Single().ProcessingStatus);
    }

    [Fact]
    public async Task Abandoned_upload_validation_claim_is_recovered_and_does_not_block_later_completion()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 600);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");
        fixture.AudioStore.PauseNextCompletion();

        var abandoned = audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        await fixture.AudioStore.WaitUntilCompletionStartedAsync();

        fixture.Clock.Set(StartTime.AddMinutes(3));
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
        var released = fixture.Db.InterviewTurns.Single();
        Assert.Equal(TurnProcessingStatus.Retry, released.ProcessingStatus);
        Assert.Null(released.ProcessingToken);
        Assert.Equal(StartTime, released.AudioCompletionReceivedAt);

        fixture.AudioStore.ReleaseCompletion();
        var abandonedResult = await abandoned;
        Assert.True(abandonedResult.Duplicate);

        fixture.Clock.Set(released.NextRetryAt!.Value);
        var recovered = await audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        Assert.Equal(SubmitOutcome.Completed, recovered.Outcome);
        Assert.Equal(TurnProcessingStatus.Completed, fixture.Db.InterviewTurns.Single().ProcessingStatus);
    }

    [Fact]
    public async Task Audio_validation_retry_preserves_original_receipt_after_deadline()
    {
        await using var fixture = await CreateFixtureAsync(timeLimitSeconds: 1,
            maximumTurnRetries: 2, initialRetryDelaySeconds: 0, maximumRetryDelaySeconds: 0);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");
        fixture.AudioStore.PauseNextCompletion();
        var abandoned = audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        await fixture.AudioStore.WaitUntilCompletionStartedAsync();
        fixture.Clock.Set(StartTime.AddMinutes(3));
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
        var pending = fixture.Db.InterviewTurns.Single();
        Assert.Equal(TurnProcessingStatus.Retry, pending.ProcessingStatus);
        Assert.Equal(StartTime, pending.AudioCompletionReceivedAt);
        fixture.AudioStore.ReleaseCompletion();
        Assert.True((await abandoned).Duplicate);
        var result = await audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        Assert.Equal(SubmitOutcome.Completed, result.Outcome);
        Assert.Equal(StartTime, fixture.Db.InterviewTurns.Single().AudioAcceptedAt);
    }

    [Fact]
    public async Task Audio_transcription_result_is_rejected_when_processing_token_is_reclaimed()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");

        fixture.SpeechToText.PauseNextCall();
        var completion = audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId);
        await fixture.SpeechToText.WaitUntilPausedAsync();

        await using (var db = fixture.CreateIndependentDb())
        {
            var turn = await db.InterviewTurns.SingleAsync();
            turn.ProcessingToken = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        fixture.SpeechToText.Release();
        var duplicate = await completion;

        Assert.True(duplicate.Duplicate);
        var stored = fixture.Db.InterviewTurns.Single();
        Assert.Null(stored.AnswerTranscript);
        Assert.Null(stored.AnsweredAt);
        Assert.Equal(TurnProcessingStatus.Processing, stored.ProcessingStatus);
        Assert.Equal(0, fixture.Scorer.CallCount);
        Assert.Empty(fixture.Db.CriterionScores);
    }

    [Fact]
    public async Task Transcription_retries_are_bounded_and_stop_after_the_configured_limit()
    {
        await using var fixture = await CreateFixtureAsync(
            maximumTurnRetries: 1, initialRetryDelaySeconds: 0, maximumRetryDelaySeconds: 0);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");

        fixture.SpeechToText.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId));
        var retryTurn = fixture.Db.InterviewTurns.Single();
        Assert.Equal(TurnProcessingStatus.Retry, retryTurn.ProcessingStatus);
        Assert.Equal(1, retryTurn.TranscriptionRetryCount);

        fixture.Clock.Set(retryTurn.NextRetryAt!.Value);
        fixture.SpeechToText.FailNext = true;
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());

        var failed = fixture.Db.InterviewTurns.Single();
        Assert.Equal(TurnProcessingStatus.Failed, failed.ProcessingStatus);
        Assert.Equal(2, failed.TranscriptionRetryCount);
        Assert.Null(failed.NextRetryAt);
        Assert.Equal(AttemptStatus.Interrupted, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
    }

    [Fact]
    public async Task Missing_audio_on_transcription_retry_marks_the_turn_failed_once_budget_is_exhausted_safely()
    {
        await using var fixture = await CreateFixtureAsync(maximumTurnRetries: 1);
        var started = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var turnId = started.State.CurrentTurn!.TurnId;
        var audio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await audio.SendAudioChunkAsync(started.State.AttemptId, turnId, 0, [1, 2, 3], "audio/webm");

        fixture.SpeechToText.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            audio.CompleteAudioAnswerAsync(started.State.AttemptId, turnId));

        var retryTurn = fixture.Db.InterviewTurns.Single();
        Assert.Equal(TurnProcessingStatus.Retry, retryTurn.ProcessingStatus);
        Assert.Equal(1, retryTurn.TranscriptionRetryCount);
        Assert.NotNull(retryTurn.NextRetryAt);

        fixture.AudioStore.RemoveUpload(started.State.AttemptId, turnId);
        fixture.Clock.Set(retryTurn.NextRetryAt!.Value);
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());

        var failed = fixture.Db.InterviewTurns.Single();
        Assert.Equal(TurnProcessingStatus.Failed, failed.ProcessingStatus);
        Assert.Null(failed.NextRetryAt);
        Assert.Equal(AttemptStatus.Interrupted, fixture.Db.InterviewAttempts.Single().Status);
        Assert.Equal(0, await fixture.Service.ExpireOverdueAttemptsAsync());
    }

    [Fact]
    public async Task Expiry_sweep_continues_when_one_transcription_recovery_fails_and_another_attempt_is_due()
    {
        await using var fixture = await CreateFixtureAsync(
            maximumTurnRetries: 1, initialRetryDelaySeconds: 0, maximumRetryDelaySeconds: 0);
        var firstAttempt = await fixture.Service.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        var firstTurnId = firstAttempt.State.CurrentTurn!.TurnId;
        var firstAudio = fixture.CreateAudioService(new TestUser(
            fixture.Db.Participants.Single().StudentId, Roles.Student));
        await firstAudio.SendAudioChunkAsync(firstAttempt.State.AttemptId, firstTurnId, 0, [1, 2, 3], "audio/webm");
        fixture.SpeechToText.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firstAudio.CompleteAudioAnswerAsync(firstAttempt.State.AttemptId, firstTurnId));
        fixture.AudioStore.RemoveUpload(firstAttempt.State.AttemptId, firstTurnId);

        var secondStudentId = Guid.NewGuid();
        fixture.Db.Participants.Add(new Participant
        {
            Id = Guid.NewGuid(),
            ExamSessionId = fixture.Session.Id,
            StudentId = secondStudentId,
            JoinedAt = StartTime,
            Status = ParticipantStatus.Registered
        });
        await fixture.Db.SaveChangesAsync();

        var secondService = fixture.CreateService(new TestUser(secondStudentId, Roles.Student));
        var secondAttempt = await secondService.StartAttemptAsync(new StartAttemptRequest(fixture.Session.Id));
        fixture.Scorer.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => secondService.SubmitAnswerAsync(
            secondAttempt.State.AttemptId, new SubmitAnswerRequest("Retryable", secondAttempt.State.CurrentTurn!.TurnId)));

        var dueAt = fixture.Db.InterviewTurns
            .Where(turn => turn.Id == firstTurnId || turn.Id == secondAttempt.State.CurrentTurn!.TurnId)
            .Select(turn => turn.NextRetryAt)
            .Max()!.Value;
        fixture.Clock.Set(dueAt);

        var processed = await fixture.Service.ExpireOverdueAttemptsAsync();

        var firstTurn = fixture.Db.InterviewTurns.Single(turn => turn.Id == firstTurnId);
        Assert.Equal(TurnProcessingStatus.Failed, firstTurn.ProcessingStatus);
        Assert.Equal(AttemptStatus.Interrupted,
            fixture.Db.InterviewAttempts.Single(attempt => attempt.Id == firstAttempt.State.AttemptId).Status);

        var secondTurn = fixture.Db.InterviewTurns.Single(turn => turn.Id == secondAttempt.State.CurrentTurn!.TurnId);
        Assert.Equal(TurnProcessingStatus.Completed, secondTurn.ProcessingStatus);
        Assert.Equal(AttemptStatus.PendingReview,
            fixture.Db.InterviewAttempts.Single(attempt => attempt.Id == secondAttempt.State.AttemptId).Status);
        Assert.Equal(1, processed);
    }
}
