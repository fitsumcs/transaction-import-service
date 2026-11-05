using System.Globalization;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using TransactionImport.Application.Processing;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.UnitTests.Processing;

public sealed class ProcessingPropertyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly TransactionLineParser Parser = new(new ValidationOptions());

    [Property(MaxTest = 500)]
    public void Parser_never_throws_on_arbitrary_bytes(byte[] bytes)
    {
        var result = Parser.Parse(bytes ?? [], 1, Now);

        Assert.True(result.Kind is ParsedLineKind.Skipped or ParsedLineKind.Rejected or ParsedLineKind.Accepted);
    }

    [Property(MaxTest = 300)]
    public void Parser_never_throws_on_arbitrary_strings_inside_valid_json_fields(NonNull<string> value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value.Get);
        var line = $$"""{"transactionId":{{json}},"accountId":{{json}},"merchantId":"m","amount":1,"currency":{{json}},"timestamp":{{json}},"description":{{json}}}""";

        var result = Parser.Parse(Encoding.UTF8.GetBytes(line), 1, Now);

        Assert.NotEqual(ParsedLineKind.Skipped, result.Kind);
    }

    [Property(MaxTest = 200)]
    public void Accepted_records_are_already_normalized(PositiveInt cents, NonNegativeInt trailingZeros, bool lowerCaseCurrency)
    {
        var amount = cents.Get / 100m;
        var amountText = amount.ToString("0.00", CultureInfo.InvariantCulture) + new string('0', trailingZeros.Get % 5);
        var currency = lowerCaseCurrency ? " usd " : "USD";
        var line = $$"""{"transactionId":"t-{{cents.Get}}","accountId":"a","merchantId":"m","amount":{{amountText}},"currency":"{{currency}}","timestamp":"2026-07-20T10:25:00+02:00"}""";

        var transaction = Parser.Parse(Encoding.UTF8.GetBytes(line), 1, Now).Transaction!;
        var reparsed = Parser.Parse(Encoding.UTF8.GetBytes(Serialize(transaction)), 1, Now).Transaction!;

        Assert.Equal(TransactionFingerprint.Compute(transaction), TransactionFingerprint.Compute(reparsed));
        Assert.Equal("USD", transaction.Currency);
    }

    [Property(MaxTest = 100)]
    public Property Risk_score_is_bounded_and_deterministic() =>
        Prop.ForAll(
            Gen.Choose(1, 10_000_000).Select(cents => cents / 100m).ToArbitrary(),
            Gen.Choose(0, 23).ToArbitrary(),
            ArbMap.Default.ArbFor<NonEmptyString>(),
            (amount, hour, merchant) =>
            {
                var merchantId = "m-" + Math.Abs(merchant.Get.GetHashCode(StringComparison.Ordinal));
                var transaction = new NormalizedTransaction("t", "a", merchantId, amount, "USD", new DateTime(2026, 1, 1, hour, 0, 0, DateTimeKind.Utc), null);
                var fingerprint = TransactionFingerprint.Compute(transaction);
                var score = RiskModel.Score(transaction, fingerprint);
                return score.Value is >= 0 and <= 100 && score == RiskModel.Score(transaction, fingerprint);
            });

    private static string Serialize(NormalizedTransaction t) =>
        $$"""{"transactionId":"{{t.TransactionId}}","accountId":"{{t.AccountId}}","merchantId":"{{t.MerchantId}}","amount":{{t.Amount.ToString(CultureInfo.InvariantCulture)}},"currency":"{{t.Currency}}","timestamp":"{{TransactionFingerprint.CanonicalTimestamp(t.OccurredAtUtc)}}"}""";
}
