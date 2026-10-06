namespace AiOralExam.SharedKernel.Errors;

/// <summary>Shared error format for every API (FOUNDATION-02, BE-PLAT-05).</summary>
public sealed record ApiError(
    string Code,
    string Message,
    string? TraceId = null,
    IDictionary<string, string[]>? Details = null);
