using System.Text;
using TransactionImport.Application.Processing;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.UnitTests.Processing;

public sealed class TransactionLineParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 8, 0, 0, TimeSpan.Zero);
    private readonly TransactionLineParser _parser = new(new ValidationOptions());

    [Fact]
    public void Accepts_and_normalizes_a_valid_record()
    {
        var result = Parse("""{"transactionId":" txn-10001 ","accountId":"acc-201","merchantId":"merchant-18","amount":145.750,"currency":"usd","timestamp":"2026-07-20T13:25:00.1234+03:00","description":"  Subscription \t payment  "}""");

        Assert.Equal(ParsedLineKind.Accepted, result.Kind);
        var transaction = result.Transaction!;
        Assert.Equal("txn-10001", transaction.TransactionId);
        Assert.Equal(145.75m, transaction.Amount);
        Assert.Equal("USD", transaction.Currency);
        Assert.Equal(new DateTime(2026, 7, 20, 10, 25, 0, 123, DateTimeKind.Utc), transaction.OccurredAtUtc);
        Assert.Equal("Subscription payment", transaction.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r")]
    public void Skips_blank_lines(string line) => Assert.Equal(ParsedLineKind.Skipped, Parse(line).Kind);

    [Theory]
    [InlineData("{not json", RejectionCode.InvalidJson)]
    [InlineData("[1,2]", RejectionCode.NotAnObject)]
    [InlineData("\"text\"", RejectionCode.NotAnObject)]
    [InlineData("""{"a":1} {"b":2}""", RejectionCode.InvalidJson)]
    [InlineData("""{"accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.MissingField)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":"1.5","currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidFieldType)]
    [InlineData("""{"transactionId":123,"accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidFieldType)]
    [InlineData("""{"transactionId":"bad id!","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidTransactionId)]
    [InlineData("""{"transactionId":"t","accountId":"","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidAccountId)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m/../x","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidMerchantId)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":0,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidAmount)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":-5,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidAmount)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1.23456,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidAmount)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1e40,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidAmount)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1,"currency":"US","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidCurrency)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1,"currency":"XYZ","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidCurrency)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20 10:25"}""", RejectionCode.InvalidTimestamp)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00"}""", RejectionCode.InvalidTimestamp)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-02-30T10:25:00Z"}""", RejectionCode.InvalidTimestamp)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"1999-12-31T23:59:59Z"}""", RejectionCode.TimestampOutOfRange)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-30T10:25:00Z"}""", RejectionCode.TimestampOutOfRange)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z","description":"bad\u0007bell"}""", RejectionCode.InvalidCharacters)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z","description":"evil\u202Etxt"}""", RejectionCode.InvalidCharacters)]
    [InlineData("""{"transactionId":"t","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z","description":"\uD800"}""", RejectionCode.InvalidJson)]
    [InlineData("""{"transactionId":"t","transactionId":"u","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""", RejectionCode.InvalidJson)]
    public void Rejects_invalid_records_with_a_stable_code(string line, RejectionCode expected)
    {
        var result = Parse(line);

        Assert.Equal(ParsedLineKind.Rejected, result.Kind);
        Assert.Equal(expected, result.Rejection!.Code);
        Assert.Equal(7, result.Rejection.LineNumber);
    }

    [Fact]
    public void Rejects_descriptions_longer_than_500_characters()
    {
        var result = Parse(Record(description: new string('d', 501)));

        Assert.Equal(RejectionCode.DescriptionTooLong, result.Rejection!.Code);
    }

    [Fact]
    public void Rejects_invalid_utf8_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes(Record()).Concat(new byte[] { 0xC3, 0x28 }).ToArray();

        var result = _parser.Parse(bytes, 1, Now);

        Assert.Equal(RejectionCode.InvalidEncoding, result.Rejection!.Code);
    }

    [Fact]
    public void Ignores_unexpected_fields_including_nested_ones()
    {
        var line = Record().TrimEnd('}') + ""","extra":{"nested":[1,2,{"x":null}]},"flag":true}""";

        var result = Parse(line);

        Assert.Equal(ParsedLineKind.Accepted, result.Kind);
    }

    [Fact]
    public void Preserves_unicode_and_removes_invisible_format_characters()
    {
        var result = Parse(Record(description: "Caf\u0065\u0301 \u200Bpayment 日本 😀"));

        Assert.Equal("Caf\u00e9 payment 日本 😀", result.Transaction!.Description);
    }

    [Fact]
    public void Whitespace_only_description_becomes_null()
    {
        var result = Parse(Record(description: "   \t  "));

        Assert.Null(result.Transaction!.Description);
    }

    [Fact]
    public void Rejection_raw_value_is_limited_and_never_contains_the_description()
    {
        var longId = new string('a', 200);
        var result = Parse($$"""{"transactionId":"{{longId}}","accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z","description":"secret memo"}""");

        Assert.Equal(RejectionCode.InvalidTransactionId, result.Rejection!.Code);
        Assert.DoesNotContain("secret", result.Rejection.RawValue, StringComparison.Ordinal);
        Assert.Contains(new string('a', 64), result.Rejection.RawValue, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 65), result.Rejection.RawValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_json_raw_value_is_a_sanitized_excerpt()
    {
        var result = Parse("{\"broken\u0001\": " + new string('z', 500));

        Assert.StartsWith("{\"excerpt\":", result.Rejection!.RawValue, StringComparison.Ordinal);
        Assert.True(result.Rejection.RawValue!.Length < 200);
        Assert.DoesNotContain("\\u0001", result.Rejection.RawValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlong_line_becomes_a_line_too_long_rejection()
    {
        var result = TransactionLineParser.ParseTooLong(3, "{\"transactionId\":\"aaa"u8);

        Assert.Equal(RejectionCode.LineTooLong, result.Rejection!.Code);
        Assert.Equal(3, result.Rejection.LineNumber);
    }

    internal static string Record(string id = "txn-1", decimal amount = 10.5m, string currency = "USD", string timestamp = "2026-07-20T10:25:00.000Z", string? description = "Payment") =>
        description is null
            ? $$"""{"transactionId":"{{id}}","accountId":"acc-1","merchantId":"merchant-1","amount":{{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"currency":"{{currency}}","timestamp":"{{timestamp}}"}"""
            : $$"""{"transactionId":"{{id}}","accountId":"acc-1","merchantId":"merchant-1","amount":{{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"currency":"{{currency}}","timestamp":"{{timestamp}}","description":{{System.Text.Json.JsonSerializer.Serialize(description)}}}""";

    private ParsedLine Parse(string line) => _parser.Parse(Encoding.UTF8.GetBytes(line), 7, Now);
}
