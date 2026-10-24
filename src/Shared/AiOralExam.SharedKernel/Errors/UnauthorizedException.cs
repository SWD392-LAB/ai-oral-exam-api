namespace AiOralExam.SharedKernel.Errors;

public sealed class UnauthorizedException(string code, string message)
    : AppException(401, code, message);
