using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TransactionImport.Domain.Transactions;

/// <summary>
/// Deterministic risk model, version 1. The score is the sum of five components, clamped to 0..100:
/// <list type="table">
/// <item><term>Amount (0-30)</term><description>Order of magnitude of the amount: &lt;1 → 0, &lt;10 → 5, &lt;100 → 10,
/// &lt;1,000 → 15, &lt;10,000 → 20, &lt;100,000 → 25, otherwise 30. Integer thresholds, no floating point.</description></item>
/// <item><term>Hour (0-20)</term><description>UTC hour: 00-04 → 20, 22-23 → 12, 05-06 → 6, otherwise 0.</description></item>
/// <item><term>Description (0-15)</term><description>Missing → 15, contains a watch-list keyword → 15,
/// shorter than 8 characters → 8, longer than 300 → 10, otherwise 0.</description></item>
/// <item><term>Merchant (0-15)</term><description>First byte of SHA-256(merchantId) modulo 16: a stable stand-in
/// for a merchant-category risk lookup.</description></item>
/// <item><term>Fingerprint (0-20)</term><description>PBKDF2-HMAC-SHA256(fingerprint, "risk-model-v1:" + merchantId,
/// <see cref="WorkFactor"/> iterations) → first 4 bytes as uint32 modulo 21. This is the deliberately CPU-heavy part.</description></item>
/// </list>
/// Every constant here is part of the model version: changing one changes scores and requires a new version.
/// </summary>
public static class RiskModel
{
    public const int Version = 1;
    public const int WorkFactor = 1_000;

    private static readonly string[] WatchListKeywords = ["gift card", "crypto", "wire transfer", "urgent", "casino"];

    public static RiskScore Score(NormalizedTransaction transaction, ReadOnlySpan<byte> fingerprint)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var points = AmountPoints(transaction.Amount)
            + HourPoints(transaction.OccurredAtUtc.Hour)
            + DescriptionPoints(transaction.Description)
            + MerchantPoints(transaction.MerchantId)
            + FingerprintPoints(fingerprint, transaction.MerchantId);
        return new RiskScore(Math.Clamp(points, RiskScore.Min, RiskScore.Max));
    }

    public static int AmountPoints(decimal amount) => amount switch
    {
        < 1m => 0,
        < 10m => 5,
        < 100m => 10,
        < 1_000m => 15,
        < 10_000m => 20,
        < 100_000m => 25,
        _ => 30,
    };

    public static int HourPoints(int utcHour) => utcHour switch
    {
        >= 0 and < 5 => 20,
        >= 22 => 12,
        >= 5 and < 7 => 6,
        _ => 0,
    };

    public static int DescriptionPoints(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return 15;
        }

        foreach (var keyword in WatchListKeywords)
        {
            if (description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return 15;
            }
        }

        return description.Length switch
        {
            < 8 => 8,
            > 300 => 10,
            _ => 0,
        };
    }

    public static int MerchantPoints(string merchantId)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(merchantId), hash);
        return hash[0] % 16;
    }

    public static int FingerprintPoints(ReadOnlySpan<byte> fingerprint, string merchantId)
    {
        var salt = Encoding.UTF8.GetBytes("risk-model-v1:" + merchantId);
        Span<byte> derived = stackalloc byte[4];
        Rfc2898DeriveBytes.Pbkdf2(fingerprint, salt, derived, WorkFactor, HashAlgorithmName.SHA256);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(derived) % 21);
    }
}
