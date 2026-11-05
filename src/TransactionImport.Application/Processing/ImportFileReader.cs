using TransactionImport.Application.Abstractions;
using TransactionImport.Application.Imports;

namespace TransactionImport.Application.Processing;

/// <summary>
/// Opens an import file at a byte offset. Single-object uploads are read directly; resumable uploads are
/// stored as ordered chunk objects and read through <see cref="ConcatenatedReadStream"/>.
/// </summary>
public sealed class ImportFileReader(IFileStorage storage)
{
    public async Task<Stream> OpenAsync(string storageKey, long offset, CancellationToken cancellationToken)
    {
        if (!StorageKeys.IsChunked(storageKey))
        {
            return await storage.OpenReadAsync(storageKey, offset, cancellationToken).ConfigureAwait(false);
        }

        var chunks = await storage.ListAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            throw new StorageObjectNotFoundException(storageKey);
        }

        return new ConcatenatedReadStream(storage, chunks, offset);
    }
}

/// <summary>Sequential, forward-only stream over several stored objects; only one object is open at a time.</summary>
internal sealed class ConcatenatedReadStream : Stream
{
    private readonly IFileStorage _storage;
    private readonly IReadOnlyList<StoredObject> _chunks;
    private int _index;
    private long _innerOffset;
    private Stream? _current;

    public ConcatenatedReadStream(IFileStorage storage, IReadOnlyList<StoredObject> chunks, long offset)
    {
        _storage = storage;
        _chunks = chunks;
        var remaining = offset;
        while (_index < chunks.Count && remaining >= chunks[_index].SizeBytes)
        {
            remaining -= chunks[_index].SizeBytes;
            _index++;
        }

        _innerOffset = remaining;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_index < _chunks.Count)
        {
            _current ??= await _storage.OpenReadAsync(_chunks[_index].Key, _innerOffset, cancellationToken).ConfigureAwait(false);
            var read = await _current.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                return read;
            }

            await _current.DisposeAsync().ConfigureAwait(false);
            _current = null;
            _innerOffset = 0;
            _index++;
        }

        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (_current is not null)
        {
            await _current.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current?.Dispose();
        }

        base.Dispose(disposing);
    }
}
