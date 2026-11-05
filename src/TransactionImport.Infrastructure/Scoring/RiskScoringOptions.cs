namespace TransactionImport.Infrastructure.Scoring;

public sealed class RiskScoringOptions
{
    public const string SectionName = "RiskScoring";

    /// <summary>Dedicated threads for CPU-bound scoring. Defaults to ProcessorCount.</summary>
    public int WorkerCount { get; set; }

    /// <summary>Maximum queued scoring requests across all imports. Further submits wait (backpressure).</summary>
    public int MaxQueuedRequests { get; set; } = 4_000;

    public int ChunkSize { get; set; } = 64;

    /// <summary>PBKDF2 iterations for the fingerprint component. Lower for demos; keep ≥1000 in production.</summary>
    public int WorkFactor { get; set; } = 1_000;
}
