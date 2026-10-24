using AiOralExam.Modules.Interview.Contracts;

namespace AiOralExam.Modules.Interview.Domain;

/// <summary>ERD: SCORE REVIEW - the FINAL score the lecturer confirms for the whole attempt.</summary>
public class ScoreReview
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public Guid ReviewerId { get; set; }
    public decimal FinalScore { get; set; }
    public string? Comment { get; set; }
    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;
    public DateTime? ReviewedAt { get; set; }
}
