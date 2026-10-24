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
