namespace AiOralExam.SharedKernel.Errors;

/// <summary>
/// Loi nghiep vu co chu dich. Middleware o Api bat va tra ve format loi chung (ApiError).
/// Moi loi co Code on dinh de FE switch theo, Message de hien cho nguoi dung.
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

public sealed class ForbiddenException(string message = "Bạn không có quyền thực hiện thao tác này.")
    : AppException(403, "forbidden", message);

public sealed class ConflictException(string code, string message)
    : AppException(409, code, message);

public sealed class UnauthorizedException(string code, string message)
    : AppException(401, code, message);

public sealed class ValidationException(IDictionary<string, string[]> errors, string message = "Dữ liệu gửi lên không hợp lệ.")
    : AppException(400, "validation_failed", message, errors);
