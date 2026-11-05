using TransactionImport.Domain.Transactions;

namespace TransactionImport.UnitTests.Domain;

public sealed class TransactionFingerprintTests
{
    internal static readonly NormalizedTransaction Sample = new(
        "txn-10001", "acc-201", "merchant-18", 145.75m, "USD",
        new DateTime(2026, 7, 20, 10, 25, 0, DateTimeKind.Utc), "Subscription payment");

    [Fact]
    public void Matches_the_published_v1_canonical_form_and_hash()
    {
        Assert.Equal(
            "tx-fp-v1;9:txn-10001;7:acc-201;11:merchant-18;6:145.75;3:USD;24:2026-07-20T10:25:00.000Z;20:Subscription payment;",
            TransactionFingerprint.CanonicalForm(Sample));
        Assert.Equal(
            "90464b750b7679846747bc633d9f85da652c7d4a4e29f1e1acc0c41924547021",
            Convert.ToHexStringLower(TransactionFingerprint.Compute(Sample)));
    }

    [Fact]
    public void Is_identical_for_logically_identical_amounts()
    {
        var withTrailingZeros = Sample with { Amount = 145.7500m };

        Assert.Equal(TransactionFingerprint.Compute(Sample), TransactionFingerprint.Compute(withTrailingZeros));
    }

    [Theory]
    [InlineData("transactionId")]
    [InlineData("accountId")]
    [InlineData("merchantId")]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("timestamp")]
    [InlineData("description")]
    public void Changes_when_any_field_changes(string field)
    {
        var changed = field switch
        {
            "transactionId" => Sample with { TransactionId = "txn-10002" },
            "accountId" => Sample with { AccountId = "acc-202" },
            "merchantId" => Sample with { MerchantId = "merchant-19" },
            "amount" => Sample with { Amount = 145.76m },
            "currency" => Sample with { Currency = "EUR" },
            "timestamp" => Sample with { OccurredAtUtc = Sample.OccurredAtUtc.AddMilliseconds(1) },
            _ => Sample with { Description = null },
        };

        Assert.NotEqual(TransactionFingerprint.Compute(Sample), TransactionFingerprint.Compute(changed));
    }

    [Fact]
    public void Length_prefixing_prevents_field_boundary_collisions()
    {
        var a = Sample with { AccountId = "ab", MerchantId = "c" };
        var b = Sample with { AccountId = "a", MerchantId = "bc" };

        Assert.NotEqual(TransactionFingerprint.Compute(a), TransactionFingerprint.Compute(b));
    }
}
