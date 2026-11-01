using Microsoft.Extensions.Logging;

namespace AiOralExam.Modules.Interview.Application.Ai;

internal sealed class ValidatedAnswerAnalyzer(
    IAnswerAnalyzer inner,
    ILogger<ValidatedAnswerAnalyzer> logger) : IAnswerAnalyzer
{
    public async Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default)
    {
        try
        {
            return AiOutputValidator.Validate(request, await inner.AnalyzeAsync(request, ct));
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
            logger.LogWarning(ex, "Answer analysis provider failed");
            throw new AiEvaluationException("Answer analysis provider failed.");
        }
    }
}
