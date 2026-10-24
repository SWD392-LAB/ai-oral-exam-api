namespace AiOralExam.Modules.Interview.Application.Ai;

public interface IRubricScorer
{
    Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default);
}
