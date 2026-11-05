namespace TransactionImport.Application.Processing;

public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    /// <summary>Records per batch. One batch = one database transaction = one checkpoint.</summary>
    public int BatchSize { get; set; } = 1_000;

    public int MaxLineBytes { get; set; } = 32 * 1024;

    /// <summary>Imports processed concurrently by one worker process.</summary>
    public int MaxConcurrentImports { get; set; } = 2;

    /// <summary>Parsed batches buffered ahead of scoring per import; bounds memory and applies backpressure to the reader.</summary>
    public int PrefetchBatches { get; set; } = 2;

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Base delay before a job released after a transient failure is picked up again (doubles per attempt).</summary>
    public TimeSpan JobRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan JobRetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound for the final state transition (complete/release/fail) after processing stops.</summary>
    public TimeSpan FinalizeTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

public interface IWorkerIdentity
{
    string WorkerId { get; }
}

public enum ProcessingOutcome
{
    Completed,
    Cancelled,
    Failed,

    /// <summary>Returned to pending (shutdown or transient failure); it will be resumed from its checkpoint.</summary>
    Released,

    LeaseLost,
    Skipped,
    NotFound,

    /// <summary>The final state could not be persisted; lease expiry and stale-job recovery will take over.</summary>
    Abandoned,
}
