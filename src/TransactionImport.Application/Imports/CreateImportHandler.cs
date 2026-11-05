using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionImport.Application.Abstractions;
using TransactionImport.Application.Errors;

namespace TransactionImport.Application.Imports;

public sealed record CreateImportCommand(string ProviderId, string IdempotencyKey, string? FileName, Stream Content, string? TraceParent);

public sealed record CreateImportResult(ImportView Import, bool Replayed);

/// <summary>
/// Creates an import from a streamed upload.
/// <list type="number">
/// <item>Reserve the idempotency key first, so concurrent requests with the same key never both upload.</item>
/// <item>Stream the body to storage through <see cref="UploadInspectionStream"/> (bounded memory, size limit, sniffing, SHA-256).</item>
/// <item>Atomically insert the import, complete the key and enqueue processing via the outbox.</item>
/// </list>
/// Any failure (including a client disconnect) deletes the partial object and releases the key so the client
/// can retry with the same key.
/// </summary>
public sealed partial class CreateImportHandler(
    IImportRepository repository,
    IFileStorage storage,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IImportMetrics metrics,
    IOptions<ImportOptions> options,
    ILogger<CreateImportHandler> logger)
{
    private readonly ImportOptions _options = options.Value;

    public async Task<CreateImportResult> HandleAsync(CreateImportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var providerId = InputRules.RequireProviderId(command.ProviderId);
        var key = InputRules.RequireIdempotencyKey(command.IdempotencyKey);
        var lockToken = ids.NewId();

        var reservation = await repository
            .ReserveIdempotencyKeyAsync(providerId, key, lockToken, _options.IdempotencyLockDuration, cancellationToken)
            .ConfigureAwait(false);

        switch (reservation.Kind)
        {
            case IdempotencyReservationKind.Replay:
                var existing = await repository.GetAsync(reservation.ExistingImportId!.Value, ProviderScope.Unrestricted, cancellationToken).ConfigureAwait(false)
                    ?? throw AppErrors.ImportNotFound();
                LogReplayed(logger, existing.Id);
                return new CreateImportResult(existing, Replayed: true);
            case IdempotencyReservationKind.InProgress:
                throw AppErrors.IdempotencyKeyInProgress();
        }

        var importId = ids.NewId();
        var storageKey = StorageKeys.ForImport(importId, timeProvider.GetUtcNow());
        metrics.UploadStarted();
        long size = 0;

        try
        {
            string sha256;
            var inspected = new UploadInspectionStream(command.Content, _options.MaxFileBytes);
            await using (inspected.ConfigureAwait(false))
            {
                await storage.WriteAsync(storageKey, inspected, cancellationToken).ConfigureAwait(false);
                size = inspected.BytesRead;
                sha256 = inspected.GetSha256Hex();
            }

            var newImport = new NewImport(importId, providerId, InputRules.SanitizeFileName(command.FileName), storageKey, size, sha256, _options.MaxProcessingAttempts);
            var created = await repository.CreateAsync(newImport, key, lockToken, command.TraceParent, cancellationToken).ConfigureAwait(false)
                ?? throw AppErrors.IdempotencyKeyInProgress();

            metrics.UploadFinished("accepted", size);
            LogCreated(logger, created.Id, size);
            return new CreateImportResult(created, Replayed: false);
        }
        catch (Exception exception)
        {
            metrics.UploadFinished(exception is AppException app ? app.Code : "error", size);
            await CleanupAsync(providerId, key, lockToken, storageKey).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CleanupAsync(string providerId, string key, Guid lockToken, string storageKey)
    {
        // The request token may already be cancelled (client disconnect); cleanup must still run.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await storage.DeleteAsync(storageKey, timeout.Token).ConfigureAwait(false);
            await repository.ReleaseIdempotencyKeyAsync(providerId, key, lockToken, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The key lock expires on its own and the retention job removes orphaned objects.
            LogCleanupFailed(logger, exception, storageKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Import {ImportId} created ({SizeBytes} bytes)")]
    private static partial void LogCreated(ILogger logger, Guid importId, long sizeBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotent replay returned import {ImportId}")]
    private static partial void LogReplayed(ILogger logger, Guid importId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Upload cleanup failed for {StorageKey}")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception, string storageKey);
}
