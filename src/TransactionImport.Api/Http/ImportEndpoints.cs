using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TransactionImport.Application.Imports;

namespace TransactionImport.Api.Http;

public static class ImportEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string ProviderHeader = "X-Provider-Id";

    public static RouteGroupBuilder MapImportEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/imports").WithTags("Imports");

        group.MapPost("/", CreateImportAsync)
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<CreateImportResponse>(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .WithName("CreateImport")
            .WithSummary("Upload an NDJSON transaction file and start asynchronous processing.");

        group.MapGet("/{id:guid}", GetImportAsync)
            .Produces<ImportStatusResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .WithName("GetImport");

        group.MapPost("/{id:guid}/cancel", CancelImportAsync)
            .Produces<CancelImportResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .WithName("CancelImport");

        group.MapPost("/{id:guid}/retry", RetryImportAsync)
            .Produces<RetryImportResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .WithName("RetryImport")
            .WithSummary("Re-queue a failed import. Processing resumes from the last committed checkpoint.");

        group.MapGet("/{id:guid}/summary", GetSummaryAsync)
            .Produces<SummaryResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .WithName("GetImportSummary");

        group.MapGet("/{id:guid}/rejections", GetRejectionsAsync)
            .Produces<RejectionPageDto>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .WithName("GetImportRejections");

        return group;
    }

    private static async Task<IResult> CreateImportAsync(
        HttpRequest request,
        CreateImportHandler handler,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Results.Json(
                new ErrorResponse(new ErrorBody("UNSUPPORTED_MEDIA_TYPE", "Content-Type must be multipart/form-data.", request.HttpContext.TraceIdentifier)),
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
        if (file is null)
        {
            return Results.Json(
                new ErrorResponse(new ErrorBody("IMPORT_FILE_REQUIRED", "A multipart file field named 'file' is required.", request.HttpContext.TraceIdentifier)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        await using var content = file.OpenReadStream();
        var result = await handler.HandleAsync(
            new CreateImportCommand(
                request.Headers[ProviderHeader].ToString(),
                request.Headers[IdempotencyHeader].ToString(),
                file.FileName,
                content,
                Activity.Current?.Id),
            cancellationToken);

        return Results.Json(ImportDtoMapper.ToCreate(result.Import), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> GetImportAsync(Guid id, ImportQueries queries, HttpRequest request, CancellationToken cancellationToken)
    {
        var import = await queries.GetAsync(id, Scope(request), cancellationToken);
        return Results.Json(ImportDtoMapper.ToStatus(import));
    }

    private static async Task<IResult> CancelImportAsync(Guid id, ImportCommands commands, HttpRequest request, CancellationToken cancellationToken)
    {
        var status = await commands.CancelAsync(id, Scope(request), cancellationToken);
        return Results.Json(ImportDtoMapper.ToCancel(id, status));
    }

    private static async Task<IResult> RetryImportAsync(Guid id, ImportCommands commands, HttpRequest request, CancellationToken cancellationToken)
    {
        var status = await commands.RetryAsync(id, Scope(request), Activity.Current?.Id, cancellationToken);
        return Results.Json(ImportDtoMapper.ToRetry(id, status));
    }

    private static async Task<IResult> GetSummaryAsync(Guid id, ImportQueries queries, HttpRequest request, CancellationToken cancellationToken)
    {
        var summary = await queries.GetSummaryAsync(id, Scope(request), cancellationToken);
        return Results.Json(ImportDtoMapper.ToSummary(summary));
    }

    private static async Task<IResult> GetRejectionsAsync(
        Guid id,
        ImportQueries queries,
        HttpRequest request,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        var page = await queries.GetRejectionsAsync(id, Scope(request), limit, cursor, cancellationToken);
        return Results.Json(ImportDtoMapper.ToRejections(page));
    }

    /// <summary>
    /// Until authentication is added, the optional X-Provider-Id header scopes reads when present.
    /// Create always requires it (enforced in the handler).
    /// </summary>
    private static ProviderScope Scope(HttpRequest request)
    {
        var provider = request.Headers[ProviderHeader].ToString();
        return string.IsNullOrWhiteSpace(provider) ? ProviderScope.Unrestricted : new ProviderScope(provider.Trim());
    }
}
