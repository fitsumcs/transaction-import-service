using System.Text.Json;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.Application.Processing;

/// <summary>
/// Same (provider, transactionId) as an accepted transaction: an identical fingerprint is a harmless
/// resubmission (DUPLICATE_TRANSACTION); a different fingerprint is a conflicting record (DUPLICATE_CONFLICT).
/// The originally accepted transaction is never modified.
/// </summary>
public static class DuplicateClassifier
{
    public static RecordRejection Classify(long lineNumber, string transactionId, ReadOnlySpan<byte> candidateFingerprint, ReadOnlySpan<byte> existingFingerprint)
    {
        var raw = JsonSerializer.Serialize(new Dictionary<string, string> { ["transactionId"] = transactionId });
        return candidateFingerprint.SequenceEqual(existingFingerprint)
            ? new RecordRejection(lineNumber, RejectionCode.DuplicateTransaction, "Transaction was already accepted for this provider.", transactionId, raw)
            : new RecordRejection(lineNumber, RejectionCode.DuplicateConflict, "Transaction ID was already accepted for this provider with different content.", transactionId, raw);
    }
}
