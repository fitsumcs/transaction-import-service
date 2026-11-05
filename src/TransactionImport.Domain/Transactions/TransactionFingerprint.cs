using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TransactionImport.Domain.Transactions;

/// <summary>
/// Version 1 fingerprint: SHA-256 over a canonical, length-prefixed encoding of normalized fields.
/// <list type="bullet">
/// <item>Fields, in order: transactionId, accountId, merchantId, amount, currency, timestamp, description.</item>
/// <item>Each field is written as <c>{utf8ByteLength}:{value}</c>, which makes the encoding unambiguous
/// regardless of field content (no separator can be forged).</item>
/// <item>amount: invariant culture, no trailing zeros (<c>145.750</c> and <c>145.75</c> are identical).</item>
/// <item>timestamp: UTC, <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>. description: normalized text or empty when absent.</item>
/// <item>The encoding starts with <c>tx-fp-v1</c>; changing any rule requires a new version so stored
/// fingerprints stay comparable (the version is persisted next to each fingerprint).</item>
/// </list>
/// The provider id is deliberately excluded: duplicate scoping is handled by the (provider, transactionId) key.
/// </summary>
public static class TransactionFingerprint
{
    public const short CurrentVersion = 1;
    public const int SizeInBytes = 32;

    public static byte[] Compute(NormalizedTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalForm(transaction)));
    }

    public static string CanonicalForm(NormalizedTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var builder = new StringBuilder("tx-fp-v1;", 256);
        Append(builder, transaction.TransactionId);
        Append(builder, transaction.AccountId);
        Append(builder, transaction.MerchantId);
        Append(builder, CanonicalAmount(transaction.Amount));
        Append(builder, transaction.Currency);
        Append(builder, CanonicalTimestamp(transaction.OccurredAtUtc));
        Append(builder, transaction.Description ?? string.Empty);
        return builder.ToString();
    }

    public static string CanonicalAmount(decimal amount) =>
        amount.ToString("0.############################", CultureInfo.InvariantCulture);

    public static string CanonicalTimestamp(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value).Append(';');
}
