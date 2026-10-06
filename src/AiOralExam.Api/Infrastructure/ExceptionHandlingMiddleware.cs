using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.Api.Infrastructure;

/// <summary>
/// Bat loi toan cuc (BE-PLAT-05): AppException -> dung status + ApiError; loi khac -> 500,
/// khong tra stack trace / secret ra ngoai.
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
            // client huy request - khong can log loi
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error on {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status500InternalServerError,
                new ApiError("internal_error", "Đã có lỗi xảy ra, vui lòng thử lại.", context.TraceIdentifier));
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
