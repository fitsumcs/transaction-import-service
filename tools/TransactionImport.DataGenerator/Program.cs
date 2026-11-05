using System.Globalization;
using System.Text;
using System.Text.Json;

// Generates NDJSON test files for local demos and benchmarks.
// Usage: dotnet run --project tools/TransactionImport.DataGenerator -- --records=500000 --output=data/sample.ndjson [--duplicates=0.02] [--invalid=0.01]

var records = 10_000;
var duplicates = 0.02;
var invalid = 0.01;
var output = "data/sample.ndjson";
var seed = 42;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg.StartsWith("--records=", StringComparison.Ordinal))
    {
        records = int.Parse(arg["--records=".Length..], CultureInfo.InvariantCulture);
    }
    else if (arg.StartsWith("--duplicates=", StringComparison.Ordinal))
    {
        duplicates = double.Parse(arg["--duplicates=".Length..], CultureInfo.InvariantCulture);
    }
    else if (arg.StartsWith("--invalid=", StringComparison.Ordinal))
    {
        invalid = double.Parse(arg["--invalid=".Length..], CultureInfo.InvariantCulture);
    }
    else if (arg.StartsWith("--output=", StringComparison.Ordinal))
    {
        output = arg["--output=".Length..];
    }
    else if (arg.StartsWith("--seed=", StringComparison.Ordinal))
    {
        seed = int.Parse(arg["--seed=".Length..], CultureInfo.InvariantCulture);
    }
    else if (arg is "-h" or "--help")
    {
        Console.WriteLine("Usage: --records=N --output=path [--duplicates=0.02] [--invalid=0.01] [--seed=42]");
        return 0;
    }
}

ArgumentOutOfRangeException.ThrowIfNegativeOrZero(records);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

var currencies = new[] { "USD", "EUR", "GBP", "JPY", "ETB", "CAD", "AUD" };
var merchants = Enumerable.Range(1, 50).Select(i => $"merchant-{i}").ToArray();
var accounts = Enumerable.Range(1, 200).Select(i => $"acc-{i}").ToArray();
var descriptions = new[]
{
    "Subscription payment",
    "Retail purchase",
    "Wire transfer",
    "Gift card top-up",
    "Crypto exchange",
    "Restaurant",
    null,
};

var rng = new Random(seed);
await using var file = new StreamWriter(output, false, new UTF8Encoding(false), 64 * 1024);
var written = 0;
var duplicateCount = 0;
var invalidCount = 0;
string? lastValid = null;

for (var i = 0; i < records; i++)
{
    string line;
    if (lastValid is not null && rng.NextDouble() < duplicates)
    {
        line = lastValid;
        duplicateCount++;
    }
    else if (rng.NextDouble() < invalid)
    {
        line = InvalidLine(rng, i);
        invalidCount++;
    }
    else
    {
        var id = $"txn-{i + 1:D8}";
        var amount = Math.Round((decimal)(rng.NextDouble() * 5_000 + 0.01), 2);
        var currency = currencies[rng.Next(currencies.Length)];
        var hour = rng.Next(24);
        var timestamp = new DateTimeOffset(2026, 7, 1 + (i % 28), hour, rng.Next(60), rng.Next(60), TimeSpan.Zero);
        var description = descriptions[rng.Next(descriptions.Length)];
        line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["transactionId"] = id,
            ["accountId"] = accounts[rng.Next(accounts.Length)],
            ["merchantId"] = merchants[rng.Next(merchants.Length)],
            ["amount"] = amount,
            ["currency"] = currency,
            ["timestamp"] = timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["description"] = description,
        });
        lastValid = line;
    }

    await file.WriteLineAsync(line);
    written++;
}

Console.WriteLine($"Wrote {written} lines to {output} (≈{duplicateCount} duplicates, ≈{invalidCount} invalid).");
return 0;

static string InvalidLine(Random rng, int i) => rng.Next(6) switch
{
    0 => "{not json",
    1 => JsonSerializer.Serialize(new { transactionId = $"bad-{i}", accountId = "a", merchantId = "m", amount = -1, currency = "USD", timestamp = "2026-07-20T10:25:00Z" }),
    2 => JsonSerializer.Serialize(new { transactionId = $"bad-{i}", accountId = "a", merchantId = "m", amount = 10, currency = "XYZ", timestamp = "2026-07-20T10:25:00Z" }),
    3 => JsonSerializer.Serialize(new { transactionId = $"bad-{i}", accountId = "a", merchantId = "m", amount = 10, currency = "USD", timestamp = "not-a-date" }),
    4 => """{"accountId":"a","merchantId":"m","amount":1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""",
    _ => "[]",
};
