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

    /// <summary>Public API / persistence representation (UPPER_SNAKE_CASE).</summary>
    public static string ToApi(this RejectionCode code) => code switch
    {
        RejectionCode.InvalidJson => "INVALID_JSON",
        RejectionCode.InvalidEncoding => "INVALID_ENCODING",
        RejectionCode.LineTooLong => "LINE_TOO_LONG",
        RejectionCode.NotAnObject => "NOT_AN_OBJECT",
        RejectionCode.MissingField => "MISSING_FIELD",
        RejectionCode.InvalidFieldType => "INVALID_FIELD_TYPE",
        RejectionCode.InvalidTransactionId => "INVALID_TRANSACTION_ID",
        RejectionCode.InvalidAccountId => "INVALID_ACCOUNT_ID",
        RejectionCode.InvalidMerchantId => "INVALID_MERCHANT_ID",
        RejectionCode.InvalidAmount => "INVALID_AMOUNT",
        RejectionCode.InvalidCurrency => "INVALID_CURRENCY",
        RejectionCode.InvalidTimestamp => "INVALID_TIMESTAMP",
        RejectionCode.TimestampOutOfRange => "TIMESTAMP_OUT_OF_RANGE",
        RejectionCode.DescriptionTooLong => "DESCRIPTION_TOO_LONG",
        RejectionCode.InvalidCharacters => "INVALID_CHARACTERS",
        RejectionCode.DuplicateTransaction => "DUPLICATE_TRANSACTION",
        RejectionCode.DuplicateConflict => "DUPLICATE_CONFLICT",
        _ => code.ToString().ToUpperInvariant(),
    };

    public static RejectionCode ParseApi(string code) => code switch
    {
        "INVALID_JSON" => RejectionCode.InvalidJson,
        "INVALID_ENCODING" => RejectionCode.InvalidEncoding,
        "LINE_TOO_LONG" => RejectionCode.LineTooLong,
        "NOT_AN_OBJECT" => RejectionCode.NotAnObject,
        "MISSING_FIELD" => RejectionCode.MissingField,
        "INVALID_FIELD_TYPE" => RejectionCode.InvalidFieldType,
        "INVALID_TRANSACTION_ID" => RejectionCode.InvalidTransactionId,
        "INVALID_ACCOUNT_ID" => RejectionCode.InvalidAccountId,
        "INVALID_MERCHANT_ID" => RejectionCode.InvalidMerchantId,
        "INVALID_AMOUNT" => RejectionCode.InvalidAmount,
        "INVALID_CURRENCY" => RejectionCode.InvalidCurrency,
        "INVALID_TIMESTAMP" => RejectionCode.InvalidTimestamp,
        "TIMESTAMP_OUT_OF_RANGE" => RejectionCode.TimestampOutOfRange,
        "DESCRIPTION_TOO_LONG" => RejectionCode.DescriptionTooLong,
        "INVALID_CHARACTERS" => RejectionCode.InvalidCharacters,
        "DUPLICATE_TRANSACTION" => RejectionCode.DuplicateTransaction,
        "DUPLICATE_CONFLICT" => RejectionCode.DuplicateConflict,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown rejection code."),
    };
}
