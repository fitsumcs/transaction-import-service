namespace TransactionImport.Domain.Imports;

/// <summary>
/// Progress counters for an import.
/// Invariants: <c>Processed == Accepted + Rejected</c> and <c>Duplicates &lt;= Rejected</c>,
/// because a duplicate is a rejected record with a duplicate reason code.
/// Empty lines are skipped and are not counted as processed.
/// </summary>
public sealed record ImportProgress(long Processed, long Accepted, long Rejected, long Duplicates)
{
    public static ImportProgress Empty { get; } = new(0, 0, 0, 0);

    public ImportProgress Add(ImportProgress batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new ImportProgress(
            Processed + batch.Processed,
            Accepted + batch.Accepted,
            Rejected + batch.Rejected,
            Duplicates + batch.Duplicates);
    }

    public static ImportProgress ForBatch(long accepted, long rejected, long duplicates)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(accepted);
        ArgumentOutOfRangeException.ThrowIfNegative(rejected);
        ArgumentOutOfRangeException.ThrowIfNegative(duplicates);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duplicates, rejected);
        return new ImportProgress(accepted + rejected, accepted, rejected, duplicates);
    }
}
