using TransactionImport.Domain.Imports;

namespace TransactionImport.Application.Processing;

public enum ClaimStatus
{
    Claimed,
    NotFound,

    /// <summary>Terminal, or actively leased by another worker.</summary>
    NotClaimable,

    /// <summary>The import was cancelling with an expired lease; cancellation was confirmed instead.</summary>
    CancelConfirmed,

    /// <summary>Attempts exhausted; the import was marked failed.</summary>
    AttemptsExhausted,
}

/// <summary>Everything a worker needs to (re)start processing from the last committed checkpoint.</summary>
public sealed record ImportJob(
    Guid ImportId,
    string ProviderId,
    string StorageKey,
    long CheckpointByteOffset,
    long CheckpointLineNumber,
    int Attempt,
    int MaxAttempts,
    ImportProgress Progress);

public sealed record ClaimResult(ClaimStatus Status, ImportJob? Job);

public enum LeaseState
{
    Active,
    CancelRequested,
    Lost,
}

public sealed record AcceptedCandidate(long LineNumber, Domain.Transactions.NormalizedTransaction Transaction, byte[] Fingerprint, Domain.Transactions.RiskScore Risk);

/// <summary>A fully prepared batch: scored candidates, already-known rejections and the checkpoint to store.</summary>
public sealed record BatchCommit(
    Guid ImportId,
    Guid LeaseToken,
    string ProviderId,
    IReadOnlyList<AcceptedCandidate> Candidates,
    IReadOnlyList<RecordRejection> Rejections,
    long CheckpointByteOffset,
    long CheckpointLineNumber,
    TimeSpan LeaseExtension);

public sealed record BatchCommitResult(LeaseState State, long Accepted, long Rejected, long Duplicates, IReadOnlyList<RecordRejection> ConflictRejections)
{
    public static BatchCommitResult Stopped(LeaseState state) => new(state, 0, 0, 0, []);
}

public enum FinishResult
{
    Done,

    /// <summary>The import moved to cancelling meanwhile; cancellation was confirmed instead.</summary>
    Cancelled,

    LeaseLost,
}

/// <summary>
/// Persistence port for the worker. Every mutation after a claim is fenced by the lease token, so a worker
/// that lost its lease can never write, and progress/checkpoint/rows are committed atomically per batch.
/// </summary>
public interface IImportProcessingStore
{
    Task<ClaimResult> TryClaimAsync(Guid importId, string workerId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<LeaseState> RenewLeaseAsync(Guid importId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Fingerprints of already accepted transactions for the given ids (pre-check to skip scoring known duplicates).</summary>
    Task<IReadOnlyDictionary<string, byte[]>> GetExistingFingerprintsAsync(string providerId, IReadOnlyCollection<string> transactionIds, CancellationToken cancellationToken);

    Task<BatchCommitResult> CommitBatchAsync(BatchCommit batch, CancellationToken cancellationToken);

    Task<FinishResult> CompleteAsync(Guid importId, Guid leaseToken, CancellationToken cancellationToken);

    Task<FinishResult> ConfirmCancelledAsync(Guid importId, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>Returns the import to pending (resumable from its checkpoint) and re-enqueues it after <paramref name="delay"/>.</summary>
    Task<FinishResult> ReleaseAsync(Guid importId, Guid leaseToken, TimeSpan delay, CancellationToken cancellationToken);

    Task<FinishResult> FailAsync(Guid importId, Guid leaseToken, string failureCode, string failureReason, CancellationToken cancellationToken);

    /// <summary>Marks a not-currently-leased, non-terminal import as failed (used for dead-lettered messages).</summary>
    Task FailUnleasedAsync(Guid importId, string failureCode, string failureReason, CancellationToken cancellationToken);

    /// <summary>Recovers imports whose lease expired (crashed worker) and re-enqueues pending imports whose notification may have been lost.</summary>
    Task<int> RecoverStaleAsync(TimeSpan lostNotificationAge, CancellationToken cancellationToken);
}
