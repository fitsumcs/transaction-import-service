using TransactionImport.Application.Abstractions;

namespace TransactionImport.Api.Http;

/// <summary>
/// Assigns a server-generated request id (client-supplied ids are not trusted), exposes it via
/// the <c>X-Request-Id</c> response header and adds it to the logging scope.
/// </summary>
internal sealed class RequestIdMiddleware(RequestDelegate next, IIdGenerator idGenerator, ILogger<RequestIdMiddleware> logger)
{
    public const string HeaderName = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = idGenerator.NewId().ToString("N");
        context.TraceIdentifier = requestId;
        context.Response.Headers[HeaderName] = requestId;

        using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId }))
        {
            await next(context);
        }
    }
}
