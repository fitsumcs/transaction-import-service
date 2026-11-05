using TransactionImport.Application.Processing;
using TransactionImport.Domain.Imports;

namespace TransactionImport.Application.Imports;

public sealed record ImportView(
    Guid Id,
    string ProviderId,
    ImportStatus Status,
    string FileName,
    long FileSizeBytes,
    ImportProgress Progress,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    string? FailureReason);

public sealed record NewImport(
    Guid Id,
    string ProviderId,
    string FileName,
    string StorageKey,
    long FileSizeBytes,
    string FileSha256,
    int MaxAttempts);

public sealed record CurrencyTotal(string Currency, long TransactionCount, decimal TotalAmount);

public sealed record GroupTotal(string Id, string Currency, long TransactionCount, decimal TotalAmount);

public sealed record RiskLevelCounts(long Low, long Medium, long High);

/// <summary>
/// Reconciliation summary computed from persisted transactions. <see cref="IsFinal"/> is true when it is the
/// snapshot materialized at the end of processing; otherwise it is computed live while processing runs.
/// </summary>
public sealed record ImportSummary(
    Guid ImportId,
    long Accepted,
    long Rejected,
    long Duplicates,
    IReadOnlyList<CurrencyTotal> ByCurrency,
    RiskLevelCounts ByRiskLevel,
    IReadOnlyList<GroupTotal> TopMerchants,
    IReadOnlyList<GroupTotal> TopAccounts,
    bool IsFinal);

public sealed record RejectionPage(IReadOnlyList<RecordRejection> Items, string? NextCursor);

public enum IdempotencyReservationKind
{
    Reserved,
    Replay,
    InProgress,
}

public sealed record IdempotencyReservation(IdempotencyReservationKind Kind, Guid? ExistingImportId);

/// <summary>Restricts reads to one provider when authentication is enabled; <c>null</c> means unrestricted.</summary>
public sealed record ProviderScope(string? ProviderId)
{
    public static ProviderScope Unrestricted { get; } = new((string?)null);
}
