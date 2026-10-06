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

    /// <summary>POST /api/interview-attempts (BE-PLAT-04). Tra ve (state, created).</summary>
    Task<(AttemptStateDto State, bool Created)> StartAttemptAsync(StartAttemptRequest request, CancellationToken ct = default);

    /// <summary>POST /api/interview-attempts/{id}/responses (BE-AI-05).</summary>
    Task<SubmitAnswerResponse> SubmitAnswerAsync(Guid attemptId, SubmitAnswerRequest request, CancellationToken ct = default);

    Task<AttemptStateDto> GetAttemptStateAsync(Guid attemptId, CancellationToken ct = default);
}

/// <summary>
/// Dieu phoi luong thi:
///   Start  -> tao InterviewAttempt + QuestionResponse cau 1 + turn Main
///   Submit -> luu transcript -> phan tich (IAnswerAnalyzer) -> hoi xoay HOAC cham diem (IRubricScorer)
///          -> sang cau tiep theo HOAC ket thuc luot thi (PendingReview)
/// Tat ca thay doi cua 1 lan Submit duoc luu trong 1 transaction (1 SaveChanges).
/// </summary>
internal sealed class InterviewService(
    InterviewDbContext db,
    IExamConfigurationApi examConfig,
    IAnswerAnalyzer analyzer,
    IRubricScorer scorer,
    ICurrentUser currentUser,
    TimeProvider clock,
    IOptions<InterviewOptions> options,
    ILogger<InterviewService> logger) : IInterviewService
{
    private const string DefaultLanguage = "Vietnamese"; // TODO (M2): lay tu AiServiceConfig qua contract F7
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
            .Where(s => s.Status != ExamSessionStatus.Draft)   // sinh vien khong thay phien thi nhap
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
                    s.Questions.Count, s.TimeLimitPerQuestion, p.Status.ToString(),
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
                ["examSessionId"] = ["examSessionId là bắt buộc."]
            });

        var me = currentUser.Id;
        var session = await examConfig.GetSessionAsync(request.ExamSessionId, ct)
                      ?? throw new NotFoundException("exam_session_not_found", "Không tìm thấy phiên thi.");

        var participant = await db.Participants
                              .Include(p => p.Attempts)
                              .FirstOrDefaultAsync(p => p.ExamSessionId == session.Id && p.StudentId == me, ct)
                          ?? throw new ForbiddenException("Bạn chưa được đăng ký vào phiên thi này.");

        // Dang thi do (vd. F5 trang) -> tra lai luot thi cu, khong tao moi
        var running = participant.Attempts.FirstOrDefault(a => a.Status == AttemptStatus.InProgress);
        if (running is not null)
            return (await GetAttemptStateAsync(running.Id, ct), false);

        var now = UtcNow;
        if (!session.IsOpenAt(now))
            throw new ConflictException("exam_session_not_open",
                session.Status != ExamSessionStatus.Published
                    ? "Phiên thi chưa được mở."
                    : $"Phiên thi chỉ mở từ {session.StartTime:u} đến {session.EndTime:u} (UTC).");

        if (participant.Attempts.Count > 0 && !_options.AllowRetake)
            throw new ConflictException("attempt_already_taken", "Bạn đã hoàn thành lượt thi của phiên thi này.");

        if (session.Questions.Count == 0)
            throw new ConflictException("exam_session_has_no_questions", "Phiên thi chưa có câu hỏi.");

        var attempt = new InterviewAttempt
        {
            Id = Guid.NewGuid(),
            ParticipantId = participant.Id,
            StartedAt = now,
        };
        participant.Attempts.Add(attempt);
        db.InterviewAttempts.Add(attempt);
        participant.Status = ParticipantStatus.InProgress;
        participant.JoinedAt ??= now;

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
                      ?? throw new NotFoundException("exam_session_not_found", "Không tìm thấy phiên thi.");

        var response = attempt.CurrentResponse
                       ?? throw new ConflictException("no_open_question", "Không có câu hỏi nào đang chờ trả lời.");
        var turn = response.CurrentTurn
                   ?? throw new ConflictException("no_open_turn", "Câu hỏi hiện tại đã được trả lời.");
        var question = session.Questions.First(q => q.Id == response.QuestionId);

        // 1) Luu cau tra loi. Qua thoi gian + grace => coi nhu het gio, khong luu transcript.
        var now = UtcNow;
        var deadline = turn.AskedAt.AddSeconds(session.TimeLimitPerQuestion + _options.TimeLimitGraceSeconds);
        var timedOut = now > deadline;
        var answer = string.IsNullOrWhiteSpace(request.AnswerText) ? null : request.AnswerText.Trim();

        if (!timedOut && answer is not null)
        {
            turn.AnswerTranscript = answer;
            turn.AnsweredAt = now;
        }
        // het gio / bo trong: giu AnswerTranscript = null, AnsweredAt = null (giong seed)

        var dialogue = response.Turns.OrderBy(t => t.TurnNo)
            .Select(t => new DialogueTurn(t.QuestionText, t.AnswerTranscript, t.Type == TurnType.FollowUp))
            .ToList();

        // 2) Phan tich -> hoi xoay?
        var analysis = await analyzer.AnalyzeAsync(new AnswerAnalysisRequest(
            question.Content, question.RubricCriteria, dialogue,
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

        // 3) Cau hoi chinh xong -> cham diem theo rubric (luu 1 lan, Reporting chi doc lai)
        var score = await scorer.ScoreAsync(new RubricScoringRequest(
            question.Content, question.RubricCriteria, question.MaxScore, dialogue, DefaultLanguage), ct);

        var suggested = Math.Clamp(score.SuggestedScore, 0, question.MaxScore);
        response.IsFinished = true;
        response.Evaluation = new AiEvaluation
        {
            Id = Guid.NewGuid(),
            QuestionResponseId = response.Id,
            SuggestedScore = suggested,
            Feedback = score.Feedback,
            EvaluatedAt = now,
        };

        // 4) Cau tiep theo hoac ket thuc
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
                      ?? throw new NotFoundException("exam_session_not_found", "Không tìm thấy phiên thi.");
        return ToState(attempt, session);
    }

    // ------------------------------------------------------------------
    private async Task<InterviewAttempt> LoadOwnAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        var attempt = await db.InterviewAttempts
                          .Include(a => a.Participant)
                          .Include(a => a.Responses).ThenInclude(r => r.Turns)
                          .Include(a => a.Responses).ThenInclude(r => r.Evaluation)
                          .AsSplitQuery()
                          .FirstOrDefaultAsync(a => a.Id == attemptId, ct)
                      ?? throw new NotFoundException("attempt_not_found", "Không tìm thấy lượt thi.");

        // Sinh vien chi thay luot thi cua minh
        if (attempt.Participant.StudentId != currentUser.Id)
            throw new NotFoundException("attempt_not_found", "Không tìm thấy lượt thi.");
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
        // Add tuong minh: tranh EF hieu nham entity moi (Guid da gan) la entity cu can UPDATE
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
                turn.AskedAt, turn.AskedAt.AddSeconds(session.TimeLimitPerQuestion));
        }

        return new AttemptStateDto(
            attempt.Id, session.Id, session.Title, attempt.Status.ToString(),
            session.TimeLimitPerQuestion, session.MaxFollowUps, session.Questions.Count,
            attempt.Responses.Count(r => r.IsFinished),
            attempt.StartedAt, attempt.CompletedAt, current);
    }
}
