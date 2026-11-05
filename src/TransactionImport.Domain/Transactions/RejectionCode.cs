namespace TransactionImport.Domain.Transactions;

/// <summary>
/// Machine-readable reasons a record was rejected. Serialized as UPPER_SNAKE_CASE
/// (e.g. <c>INVALID_CURRENCY</c>); names are part of the public API contract.
/// </summary>
public enum RejectionCode
{
    InvalidJson,
    InvalidEncoding,
    LineTooLong,
    NotAnObject,
    MissingField,
    InvalidFieldType,
    InvalidTransactionId,
    InvalidAccountId,
    InvalidMerchantId,
    InvalidAmount,
    InvalidCurrency,
    InvalidTimestamp,
    TimestampOutOfRange,
    DescriptionTooLong,
    InvalidCharacters,

    /// <summary>Same transactionId already accepted for the provider with an identical fingerprint.</summary>
    DuplicateTransaction,

    /// <summary>Same transactionId already accepted for the provider with different content.</summary>
    DuplicateConflict,
}

public static class RejectionCodeExtensions
{
    public static bool IsDuplicate(this RejectionCode code) =>
        code is RejectionCode.DuplicateTransaction or RejectionCode.DuplicateConflict;
}
