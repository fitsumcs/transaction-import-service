using Microsoft.AspNetCore.Diagnostics;
using TransactionImport.Domain;

namespace TransactionImport.Api.Http;

/// <summary>
/// Last line of defence: converts exceptions into the public error envelope without leaking
/// stack traces, SQL, file paths or exception types. Unknown exceptions are treated as
/// programming errors (500) and logged in full.
/// </summary>
internal sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, message) = exception switch
        {
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                (StatusCodes.Status413PayloadTooLarge, "REQUEST_BODY_TOO_LARGE", "The request body exceeds the allowed size."),
            BadHttpRequestException bad =>
                (bad.StatusCode, "BAD_REQUEST", "The request is malformed."),
            DomainException domain =>
                (StatusCodes.Status409Conflict, domain.Code, domain.Message),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested =>
                (499, "REQUEST_ABORTED", "The client closed the request."),
            _ =>
                (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred."),
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, exception, code);
        }
        else
        {
            LogHandled(logger, code, status);
        }

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(
            new ErrorResponse(new ErrorBody(code, message, httpContext.TraceIdentifier)),
            cancellationToken);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception mapped to {ErrorCode}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request failed with {ErrorCode} ({StatusCode})")]
    private static partial void LogHandled(ILogger logger, string errorCode, int statusCode);
}
