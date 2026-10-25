namespace AiOralExam.SharedKernel.Errors;

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : AppException(403, "forbidden", message);
