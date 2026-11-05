using Microsoft.Extensions.Logging;
using TransactionImport.Application.Errors;
using TransactionImport.Domain.Imports;

namespace TransactionImport.Application.Imports;

public sealed partial class ImportCommands(IImportRepository repository, ILogger<ImportCommands> logger)
{
    /// <summary>
    /// Pending imports are cancelled immediately. Processing imports move to <c>cancelling</c>; the worker
    /// stops at the next batch boundary without committing further data and confirms <c>cancelled</c>.
    /// Repeated requests are idempotent.
    /// </summary>
    public async Task<ImportStatus> CancelAsync(Guid importId, ProviderScope scope, CancellationToken cancellationToken)
    {
        var result = await repository.RequestCancellationAsync(importId, scope, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case CancelOutcome.NotFound:
                throw AppErrors.ImportNotFound();
            case CancelOutcome.NotCancellable:
                throw AppErrors.ImportNotCancellable(ToApi(result.Status!.Value));
            default:
                LogCancel(logger, importId, result.Outcome);
                return result.Status!.Value;
        }
    }

    /// <summary>Re-queues a failed import. Processing resumes from the last committed checkpoint with a fresh attempt budget.</summary>
    public async Task<ImportStatus> RetryAsync(Guid importId, ProviderScope scope, string? traceParent, CancellationToken cancellationToken)
    {
        var result = await repository.RetryAsync(importId, scope, traceParent, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case RetryOutcome.NotFound:
                throw AppErrors.ImportNotFound();
            case RetryOutcome.NotRetryable:
                throw AppErrors.ImportNotRetryable(ToApi(result.Status!.Value));
            default:
                LogRetry(logger, importId);
                return result.Status!.Value;
        }
    }

    private static string ToApi(ImportStatus status) => status.ToString().ToLowerInvariant();

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancellation for import {ImportId}: {Outcome}")]
    private static partial void LogCancel(ILogger logger, Guid importId, CancelOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import {ImportId} re-queued by retry request")]
    private static partial void LogRetry(ILogger logger, Guid importId);
}
