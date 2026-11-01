using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.Modules.Interview.Application.Ai;

/// <summary>
/// A provider response cannot safely be used for scoring or follow-up generation.
/// The answer remains un-evaluated so the operation can be retried.
/// </summary>
public sealed class AiEvaluationException(string detail)
    : AppException(503, "ai_evaluation_failed", "AI evaluation could not be completed. Please retry.",
        new Dictionary<string, string[]> { ["evaluation"] = [detail] });
