using Microsoft.Extensions.Logging;

namespace AiOralExam.Modules.Interview.Application.Ai;

internal sealed class ValidatedRubricScorer(
    IRubricScorer inner,
    ILogger<ValidatedRubricScorer> logger) : IRubricScorer
{
    public async Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default)
    {
        try
        {
            return AiOutputValidator.Validate(request, await inner.ScoreAsync(request, ct));
        }
        catch (AiEvaluationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rubric scorer provider failed");
            throw new AiEvaluationException("Rubric scorer provider failed.");
        }
    }
}
