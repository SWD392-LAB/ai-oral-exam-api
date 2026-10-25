namespace AiOralExam.Modules.Interview.Application.Ai;

public interface IAnswerAnalyzer
{
    Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default);
}
