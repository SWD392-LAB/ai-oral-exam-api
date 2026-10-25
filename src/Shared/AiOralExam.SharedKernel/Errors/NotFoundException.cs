namespace AiOralExam.SharedKernel.Errors;

public sealed class NotFoundException(string code, string message)
    : AppException(404, code, message);
