using TransactionImport.Domain.Transactions;

namespace TransactionImport.Application.Processing;

public enum ParsedLineKind
{
    Skipped,
    Accepted,
    Rejected,
}

/// <summary>A rejected record. <see cref="RawValue"/> is a size-limited JSON object, never the full line.</summary>
public sealed record RecordRejection(long LineNumber, RejectionCode Code, string Message, string? TransactionId, string? RawValue);

public readonly record struct ParsedLine(ParsedLineKind Kind, NormalizedTransaction? Transaction, RecordRejection? Rejection)
{
    public static ParsedLine Skipped { get; } = new(ParsedLineKind.Skipped, null, null);

    public static ParsedLine Accept(NormalizedTransaction transaction) => new(ParsedLineKind.Accepted, transaction, null);

    public static ParsedLine Reject(RecordRejection rejection) => new(ParsedLineKind.Rejected, null, rejection);
}
