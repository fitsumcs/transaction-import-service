using System.Buffers;

namespace TransactionImport.Application.Processing;

/// <summary>
/// Splits a byte stream into lines without ever holding more than one buffer in memory.
/// <list type="bullet">
/// <item>Works on bytes, so multi-byte UTF-8 sequences are never split by the reader.</item>
/// <item>Lines longer than <c>maxLineBytes</c> are reported once with <see cref="IsTooLong"/> and a short
/// excerpt; the remainder is skipped without buffering.</item>
/// <item>A final line without a trailing newline is returned.</item>
/// <item><see cref="EndOffset"/> is the absolute byte offset just past the current line, used as the
/// resume checkpoint. A UTF-8 BOM at offset 0 is skipped.</item>
/// </list>
/// <see cref="Current"/> is only valid until the next call to <see cref="ReadNextAsync"/>.
/// </summary>
public sealed class NdjsonLineReader : IDisposable
{
    public const int ExcerptBytes = 120;

    private readonly Stream _stream;
    private readonly int _maxLineBytes;
    private readonly byte[] _excerpt = new byte[ExcerptBytes];
    private byte[] _buffer;
    private int _start;
    private int _end;
    private int _lineStart;
    private int _lineLength;
    private int _excerptLength;
    private long _offset;
    private bool _eof;
    private bool _bomChecked;

    public NdjsonLineReader(Stream stream, int maxLineBytes, long startOffset = 0, long startLineNumber = 0, int bufferSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 1);
        _stream = stream;
        _maxLineBytes = maxLineBytes;
        _offset = startOffset;
        _bomChecked = startOffset > 0;
        LineNumber = startLineNumber;
        EndOffset = startOffset;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(bufferSize, maxLineBytes + 1));
    }

    public long LineNumber { get; private set; }

    public long EndOffset { get; private set; }

    public bool IsTooLong { get; private set; }

    public ReadOnlySpan<byte> Current => IsTooLong
        ? _excerpt.AsSpan(0, _excerptLength)
        : _buffer.AsSpan(_lineStart, _lineLength);

    public async ValueTask<bool> ReadNextAsync(CancellationToken cancellationToken)
    {
        var skipping = false;
        while (true)
        {
            var available = _buffer.AsSpan(_start, _end - _start);
            var newline = available.IndexOf((byte)'\n');

            if (newline >= 0)
            {
                if (!skipping && newline > _maxLineBytes)
                {
                    CaptureExcerpt(available[..newline]);
                    skipping = true;
                }

                if (skipping)
                {
                    Advance(newline + 1);
                    return Emit(tooLong: true);
                }

                _lineStart = _start;
                _lineLength = newline;
                Advance(newline + 1);
                return Emit(tooLong: false);
            }

            if (!skipping && available.Length > _maxLineBytes)
            {
                CaptureExcerpt(available);
                skipping = true;
            }

            if (skipping)
            {
                Advance(available.Length);
            }

            if (_eof)
            {
                if (skipping)
                {
                    return Emit(tooLong: true);
                }

                if (_end > _start)
                {
                    _lineStart = _start;
                    _lineLength = _end - _start;
                    Advance(_lineLength);
                    return Emit(tooLong: false);
                }

                return false;
            }

            await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0)
        {
            var length = _end - _start;
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, length);
            _start = 0;
            _end = length;
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            _eof = true;
            return;
        }

        _end += read;

        if (!_bomChecked && _end - _start >= 3)
        {
            _bomChecked = true;
            if (_buffer.AsSpan(_start, 3).SequenceEqual("\uFEFF"u8))
            {
                Advance(3);
            }
        }
    }

    private void Advance(int count)
    {
        _start += count;
        _offset += count;
    }

    private bool Emit(bool tooLong)
    {
        IsTooLong = tooLong;
        LineNumber++;
        EndOffset = _offset;
        return true;
    }

    private void CaptureExcerpt(ReadOnlySpan<byte> line)
    {
        _excerptLength = Math.Min(ExcerptBytes, line.Length);
        line[.._excerptLength].CopyTo(_excerpt);
    }
}
