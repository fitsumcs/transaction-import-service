using System.Collections.Frozen;

namespace TransactionImport.Domain.Imports;

/// <summary>
/// The single source of truth for legal import status changes. Persistence adapters
/// use <see cref="AllowedSources"/> to guard conditional updates so that concurrent
/// actors (worker, cancel endpoint, stale-job reaper) cannot race into an invalid state.
/// </summary>
public static class ImportStatusTransitions
{
    private static readonly FrozenDictionary<ImportStatus, FrozenSet<ImportStatus>> Allowed =
        new Dictionary<ImportStatus, FrozenSet<ImportStatus>>
        {
            // Pending -> Cancelled: cancel before any worker picked the job up.
            // Pending -> Failed: job could not be started (e.g. stored file missing, attempts exhausted).
            [ImportStatus.Pending] = Set(ImportStatus.Processing, ImportStatus.Cancelled, ImportStatus.Failed),

            // Processing -> Pending: lease released on graceful shutdown or reclaimed after expiry;
            // processing resumes from the last committed checkpoint.
            [ImportStatus.Processing] = Set(
                ImportStatus.Completed,
                ImportStatus.Failed,
                ImportStatus.Cancelling,
                ImportStatus.Pending),

            // Cancelling -> Failed: the worker crashed and recovery could not confirm a clean stop.
            [ImportStatus.Cancelling] = Set(ImportStatus.Cancelled, ImportStatus.Failed),

            [ImportStatus.Completed] = Set(),

            // Failed -> Pending: explicit operator retry only (POST /v1/imports/{id}/retry).
            [ImportStatus.Failed] = Set(ImportStatus.Pending),
            [ImportStatus.Cancelled] = Set(),
        }.ToFrozenDictionary();

    private static readonly FrozenSet<ImportStatus> Terminal = Set(ImportStatus.Completed, ImportStatus.Failed, ImportStatus.Cancelled);

    public static bool CanTransition(ImportStatus from, ImportStatus to) => Allowed[from].Contains(to);

    /// <summary>Terminal statuses are never left automatically; only an explicit retry can reopen a failed import.</summary>
    public static bool IsTerminal(ImportStatus status) => Terminal.Contains(status);

    public static IReadOnlyCollection<ImportStatus> AllowedSources(ImportStatus target) =>
        Allowed.Where(pair => pair.Value.Contains(target)).Select(pair => pair.Key).ToArray();

    private static FrozenSet<ImportStatus> Set(params ImportStatus[] statuses) => statuses.ToFrozenSet();
}
