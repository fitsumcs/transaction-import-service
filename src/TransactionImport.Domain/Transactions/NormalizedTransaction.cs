namespace TransactionImport.Domain.Transactions;

/// <summary>
/// A validated transaction in canonical form: trimmed identifiers, upper-case currency, UTC timestamp
/// truncated to milliseconds, NFC-normalized description with collapsed whitespace (or <c>null</c>).
/// </summary>
public sealed record NormalizedTransaction(
    string TransactionId,
    string AccountId,
    string MerchantId,
    decimal Amount,
    string Currency,
    DateTime OccurredAtUtc,
    string? Description);
