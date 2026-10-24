namespace AiOralExam.SharedKernel.Errors;

public sealed class ConflictException(string code, string message)
    : AppException(409, code, message);
