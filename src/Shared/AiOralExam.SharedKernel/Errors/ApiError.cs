namespace AiOralExam.SharedKernel.Errors;

/// <summary>Format loi chung cho moi API (FOUNDATION-02, BE-PLAT-05).</summary>
public sealed record ApiError(
    string Code,
    string Message,
    string? TraceId = null,
    IDictionary<string, string[]>? Details = null);
