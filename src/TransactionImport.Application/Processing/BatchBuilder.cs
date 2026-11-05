using TransactionImport.Domain.Transactions;

namespace TransactionImport.Application.Processing;

public sealed record PreparedCandidate(long LineNumber, NormalizedTransaction Transaction, byte[] Fingerprint);

/// <summary>A parsed batch covering the byte range [StartOffset, EndOffset) of the file.</summary>
public sealed record PreparedBatch(
    int Number,
    long StartOffset,
    long EndOffset,
    long EndLineNumber,
    IReadOnlyList<PreparedCandidate> Candidates,
    IReadOnlyList<RecordRejection> Rejections)
{
    public int RecordCount => Candidates.Count + Rejections.Count;
}

internal sealed class BatchBuilder(long startOffset, long startLineNumber)
{
    private List<PreparedCandidate> _candidates = [];
    private List<RecordRejection> _rejections = [];
    private long _startOffset = startOffset;
    private long _endOffset = startOffset;
    private long _endLine = startLineNumber;

    public int RecordCount => _candidates.Count + _rejections.Count;

    public bool HasLines => _endOffset > _startOffset;

    public void Add(ParsedLine line, long lineNumber, long endOffset)
    {
        _endOffset = endOffset;
        _endLine = lineNumber;
        switch (line.Kind)
        {
            case ParsedLineKind.Accepted:
                _candidates.Add(new PreparedCandidate(lineNumber, line.Transaction!, TransactionFingerprint.Compute(line.Transaction!)));
                break;
            case ParsedLineKind.Rejected:
                _rejections.Add(line.Rejection!);
                break;
        }
    }

    public PreparedBatch Take(int number)
    {
        var batch = new PreparedBatch(number, _startOffset, _endOffset, _endLine, _candidates, _rejections);
        _candidates = [];
        _rejections = [];
        _startOffset = _endOffset;
        return batch;
    }
}
