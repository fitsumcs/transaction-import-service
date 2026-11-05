using Microsoft.Extensions.Options;
using TransactionImport.Application.Abstractions;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.Infrastructure.Scoring;

internal sealed class LocalRiskScorer(IOptions<RiskScoringOptions> options) : IRiskScorer
{
    private readonly int _workFactor = Math.Max(1, options.Value.WorkFactor);

    public int ModelVersion => RiskModel.Version;

    public RiskScore Score(NormalizedTransaction transaction, ReadOnlySpan<byte> fingerprint) =>
        RiskModel.Score(transaction, fingerprint, _workFactor);
}
