using TransactionImport.Domain.Imports;

namespace TransactionImport.Application.Imports;

public enum CancelOutcome
{
    NotFound,
    CancellationRequested,
    Cancelled,
    AlreadyCancelling,
    NotCancellable,
}

public sealed record CancelResult(CancelOutcome Outcome, ImportStatus? Status);

public enum RetryOutcome
{
    NotFound,
    Requeued,
    NotRetryable,
}

public sealed record RetryResult(RetryOutcome Outcome, ImportStatus? Status);

/// <summary>Persistence port for request-driven import use cases.</summary>
public interface IImportRepository
{
    /// <summary>
    /// Inserts the key as in-progress, or takes over an in-progress key whose lock expired (interrupted upload).
    /// Concurrent callers with the same key are serialized by the primary key.
    /// </summary>
    Task<IdempotencyReservation> ReserveIdempotencyKeyAsync(string providerId, string key, Guid lockToken, TimeSpan lockDuration, CancellationToken cancellationToken);

    Task ReleaseIdempotencyKeyAsync(string providerId, string key, Guid lockToken, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: inserts the import, completes the idempotency key (fenced by <paramref name="lockToken"/>)
    /// and writes the outbox message that triggers processing. Returns null if the key lock was lost.
    /// </summary>
    Task<ImportView?> CreateAsync(NewImport import, string idempotencyKey, Guid lockToken, string? traceParent, CancellationToken cancellationToken);

    Task<ImportView?> GetAsync(Guid importId, ProviderScope scope, CancellationToken cancellationToken);

    Task<CancelResult> RequestCancellationAsync(Guid importId, ProviderScope scope, CancellationToken cancellationToken);

    Task<RetryResult> RetryAsync(Guid importId, ProviderScope scope, string? traceParent, CancellationToken cancellationToken);

    Task<ImportSummary?> GetSummaryAsync(Guid importId, ProviderScope scope, int topGroups, CancellationToken cancellationToken);

    /// <summary>Returns up to <paramref name="limit"/> + 1 rejections after <paramref name="afterLine"/>, ordered by line.</summary>
    Task<IReadOnlyList<Processing.RecordRejection>?> GetRejectionsAsync(Guid importId, ProviderScope scope, long afterLine, int limit, CancellationToken cancellationToken);
}
