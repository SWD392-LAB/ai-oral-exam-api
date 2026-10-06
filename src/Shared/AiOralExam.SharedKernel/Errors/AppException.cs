namespace AiOralExam.SharedKernel.Errors;

/// <summary>
/// Expected business error. The Api middleware catches it and returns the shared error format (ApiError).
/// Every error has a stable Code for the FE to switch on, and a Message to show to the user.
/// </summary>
public class AppException : Exception
{
    public AppException(int statusCode, string code, string message, IDictionary<string, string[]>? details = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public IDictionary<string, string[]>? Details { get; }
}

public sealed class NotFoundException(string code, string message)
    : AppException(404, code, message);

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : AppException(403, "forbidden", message);

public sealed class ConflictException(string code, string message)
    : AppException(409, code, message);

public sealed class UnauthorizedException(string code, string message)
    : AppException(401, code, message);

public sealed class ValidationException(IDictionary<string, string[]> errors, string message = "The submitted data is invalid.")
    : AppException(400, "validation_failed", message, errors);
