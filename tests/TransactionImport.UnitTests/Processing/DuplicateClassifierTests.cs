using TransactionImport.Application.Processing;
using TransactionImport.Domain.Transactions;
using TransactionImport.UnitTests.Domain;

namespace TransactionImport.UnitTests.Processing;

public sealed class DuplicateClassifierTests
{
    [Fact]
    public void Identical_fingerprint_is_a_harmless_duplicate()
    {
        var fingerprint = TransactionFingerprint.Compute(TransactionFingerprintTests.Sample);

        var rejection = DuplicateClassifier.Classify(10, "txn-10001", fingerprint, fingerprint);

        Assert.Equal(RejectionCode.DuplicateTransaction, rejection.Code);
        Assert.Equal(10, rejection.LineNumber);
    }

    [Fact]
    public void Different_fingerprint_is_a_conflict()
    {
        var original = TransactionFingerprint.Compute(TransactionFingerprintTests.Sample);
        var different = TransactionFingerprint.Compute(TransactionFingerprintTests.Sample with { Amount = 1m });

        var rejection = DuplicateClassifier.Classify(11, "txn-10001", different, original);

        Assert.Equal(RejectionCode.DuplicateConflict, rejection.Code);
        Assert.Contains("txn-10001", rejection.RawValue, StringComparison.Ordinal);
    }
}
