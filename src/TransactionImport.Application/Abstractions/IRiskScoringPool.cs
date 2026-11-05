using TransactionImport.Domain.Transactions;

namespace TransactionImport.Application.Abstractions;

public interface IRiskScorer
{
    int ModelVersion { get; }

    RiskScore Score(NormalizedTransaction transaction, ReadOnlySpan<byte> fingerprint);
}

/// <summary>
/// Runs CPU-heavy risk scoring on a bounded, reusable set of workers. Callers submit whole batches; the pool
/// splits them into chunks. When the internal queue is full, <see cref="ScoreAsync"/> waits (backpressure).
/// </summary>
public interface IRiskScoringPool
{
    Task<RiskScore[]> ScoreAsync(IReadOnlyList<ScoringRequest> requests, CancellationToken cancellationToken);
}

public readonly record struct ScoringRequest(NormalizedTransaction Transaction, byte[] Fingerprint);
