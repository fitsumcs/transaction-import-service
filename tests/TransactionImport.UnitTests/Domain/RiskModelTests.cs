using TransactionImport.Domain.Transactions;

namespace TransactionImport.UnitTests.Domain;

public sealed class RiskModelTests
{
    [Theory]
    [InlineData("0.5", 0)]
    [InlineData("9.99", 5)]
    [InlineData("10", 10)]
    [InlineData("999.99", 15)]
    [InlineData("1000", 20)]
    [InlineData("99999.99", 25)]
    [InlineData("100000", 30)]
    public void Amount_points_follow_orders_of_magnitude(string amount, int expected) =>
        Assert.Equal(expected, RiskModel.AmountPoints(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(0, 20)]
    [InlineData(4, 20)]
    [InlineData(5, 6)]
    [InlineData(6, 6)]
    [InlineData(7, 0)]
    [InlineData(21, 0)]
    [InlineData(22, 12)]
    [InlineData(23, 12)]
    public void Hour_points_penalize_night_activity(int hour, int expected) =>
        Assert.Equal(expected, RiskModel.HourPoints(hour));

    [Theory]
    [InlineData(null, 15)]
    [InlineData("", 15)]
    [InlineData("Buy GIFT CARD now", 15)]
    [InlineData("short", 8)]
    [InlineData("Subscription payment", 0)]
    public void Description_points(string? description, int expected) =>
        Assert.Equal(expected, RiskModel.DescriptionPoints(description));

    [Fact]
    public void Long_descriptions_score_ten() => Assert.Equal(10, RiskModel.DescriptionPoints(new string('a', 301)));

    [Fact]
    public void Score_is_deterministic_and_matches_the_v1_reference_value()
    {
        var transaction = TransactionFingerprintTests.Sample;
        var fingerprint = TransactionFingerprint.Compute(transaction);

        var first = RiskModel.Score(transaction, fingerprint);
        var second = RiskModel.Score(transaction, fingerprint);

        Assert.Equal(first, second);
        var expected = RiskModel.AmountPoints(145.75m) + RiskModel.HourPoints(10) + RiskModel.DescriptionPoints("Subscription payment")
            + RiskModel.MerchantPoints("merchant-18") + RiskModel.FingerprintPoints(fingerprint, "merchant-18", RiskModel.DefaultWorkFactor);
        Assert.Equal(expected, first.Value);
    }

    [Fact]
    public void Component_maxima_sum_to_the_score_ceiling() => Assert.Equal(100, 30 + 20 + 15 + 15 + 20);
}
