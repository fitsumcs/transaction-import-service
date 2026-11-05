using TransactionImport.Domain.Transactions;

namespace TransactionImport.UnitTests.Domain;

public sealed class RiskScoreTests
{
    [Theory]
    [InlineData(0, RiskLevel.Low)]
    [InlineData(39, RiskLevel.Low)]
    [InlineData(40, RiskLevel.Medium)]
    [InlineData(69, RiskLevel.Medium)]
    [InlineData(70, RiskLevel.High)]
    [InlineData(100, RiskLevel.High)]
    public void Maps_score_boundaries_to_levels(int score, RiskLevel expected) =>
        Assert.Equal(expected, new RiskScore(score).Level);

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rejects_out_of_range_scores(int score) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RiskScore(score));
}
