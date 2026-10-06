using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.Api.Infrastructure;

/// <summary>
/// Global error handling (BE-PLAT-05): AppException -> its status + ApiError; anything else -> 500,
/// never leaking stack traces or secrets.
/// </summary>
internal sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex)
        {
            logger.LogInformation("Business error {Code} on {Method} {Path}: {Message}",
                ex.Code, context.Request.Method, context.Request.Path, ex.Message);
            await WriteAsync(context, ex.StatusCode, new ApiError(ex.Code, ex.Message, context.TraceIdentifier, ex.Details));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // the client cancelled the request - nothing to log
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error on {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status500InternalServerError,
                new ApiError("internal_error", "Something went wrong, please try again.", context.TraceIdentifier));
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, ApiError error)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(error);
    }
}
