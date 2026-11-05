using System.Security.Cryptography;
using TransactionImport.Application.Errors;

namespace TransactionImport.Application.Imports;

/// <summary>
/// Read-through wrapper applied to every upload. While bytes flow to storage it enforces the size limit,
/// computes SHA-256 and sniffs the content (the client MIME type and file name are not trusted):
/// gzip/zip signatures are refused (no decompression, hence no compression bombs), NUL bytes in the first
/// 8 KiB mark the file as binary, and the first non-whitespace byte must be <c>{</c>.
/// </summary>
public sealed class UploadInspectionStream : Stream
{
    private const int SniffWindow = 8 * 1024;
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly Stream _inner;
    private readonly long _maxBytes;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesRead;
    private bool _sawContent;
    private byte[]? _sha256;

    public UploadInspectionStream(Stream inner, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _maxBytes = maxBytes;
    }

    public long BytesRead => _bytesRead;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public string GetSha256Hex()
    {
        _sha256 ??= _hash.GetHashAndReset();
        return Convert.ToHexStringLower(_sha256);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Inspect(buffer.Span[..read]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => Inspect(buffer.AsSpan(offset, _inner.Read(buffer, offset, count)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Inspect(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return _sawContent ? 0 : throw AppErrors.FileEmpty();
        }

        var chunkStart = _bytesRead;
        _bytesRead += chunk.Length;
        if (_bytesRead > _maxBytes)
        {
            throw AppErrors.FileTooLarge(_maxBytes);
        }

        if (chunkStart == 0 && (chunk.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]) || chunk.StartsWith("PK"u8)))
        {
            throw AppErrors.CompressedUploadNotSupported();
        }

        if (chunkStart < SniffWindow)
        {
            var window = chunk[..(int)Math.Min(chunk.Length, SniffWindow - chunkStart)];
            if (window.Contains((byte)0))
            {
                throw AppErrors.InvalidFileType();
            }
        }

        if (!_sawContent)
        {
            for (var i = 0; i < chunk.Length; i++)
            {
                var absolute = chunkStart + i;
                var b = chunk[i];
                if (absolute < Bom.Length && b == Bom[absolute])
                {
                    continue;
                }

                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    continue;
                }

                if (b != (byte)'{')
                {
                    throw AppErrors.InvalidFileType();
                }

                _sawContent = true;
                break;
            }
        }

        _hash.AppendData(chunk);
        return chunk.Length;
    }
}
