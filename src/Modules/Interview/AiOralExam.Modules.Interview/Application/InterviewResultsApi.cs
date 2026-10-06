using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.Interview.Application;

/// <summary>Implements the stored-results contract for Reporting. Read-only, never calls the AI.</summary>
internal sealed class InterviewResultsApi(InterviewDbContext db) : IInterviewResultsApi
{
    public async Task<AttemptResult?> GetAttemptResultAsync(Guid attemptId, CancellationToken ct = default)
    {
        var attempt = await Query().FirstOrDefaultAsync(a => a.Id == attemptId, ct);
        return attempt is null ? null : ToResult(attempt);
    }

    public async Task<IReadOnlyList<AttemptResult>> GetSessionResultsAsync(Guid examSessionId, CancellationToken ct = default)
    {
        var attempts = await Query()
            .Where(a => a.Participant.ExamSessionId == examSessionId && a.Status != AttemptStatus.InProgress)
            .ToListAsync(ct);
        return attempts.Select(ToResult).ToList();
    }

    private IQueryable<InterviewAttempt> Query() => db.InterviewAttempts.AsNoTracking()
        .Include(a => a.Participant)
        .Include(a => a.Review)
        .Include(a => a.Responses).ThenInclude(r => r.Turns)
        .Include(a => a.Responses).ThenInclude(r => r.Evaluation)
        .AsSplitQuery();

    private static AttemptResult ToResult(InterviewAttempt a) => new(
        a.Id,
        a.Participant.ExamSessionId,
        a.Participant.StudentId,
        a.Status,
        a.StartedAt,
        a.CompletedAt,
        a.Review is null
            ? null
            : new ScoreReviewResult(a.Review.ReviewerId, a.Review.FinalScore, a.Review.Comment, a.Review.Status, a.Review.ReviewedAt),
        a.Responses.Select(r => new QuestionResponseResult(
                r.Id, r.QuestionId, r.FollowUpCount, r.IsFinished,
                r.Evaluation?.SuggestedScore, r.Evaluation?.Feedback,
                r.Turns.OrderBy(t => t.TurnNo)
                    .Select(t => new TurnResult(t.TurnNo, t.Type, t.QuestionText, t.AnswerTranscript, t.AskedAt, t.AnsweredAt))
                    .ToList()))
            .ToList());
}
