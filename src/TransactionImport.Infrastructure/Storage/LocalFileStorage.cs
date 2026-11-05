using TransactionImport.Application.Abstractions;

namespace TransactionImport.Infrastructure.Storage;

/// <summary>
/// Filesystem-backed object storage. Writes go to a sibling temp file and are renamed into place so
/// readers never see a partial object. Keys are treated as relative paths under the configured root;
/// absolute paths and <c>..</c> segments are rejected.
/// </summary>
internal sealed class LocalFileStorage(FileStorageOptions options) : IFileStorage
{
    private readonly string _root = Path.GetFullPath(options.LocalRoot);

    public async Task WriteAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string key, long offset, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        if (!File.Exists(path))
        {
            throw new StorageObjectNotFoundException(key);
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        return Task.FromResult<Stream>(stream);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        TryDelete(Resolve(key));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var directory = Resolve(prefix.TrimEnd('/'));
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<StoredObject>>([]);
        }

        var items = Directory.EnumerateFiles(directory)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var info = new FileInfo(path);
                var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
                return new StoredObject(relative, info.Length, info.LastWriteTimeUtc);
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<StoredObject>>(items);
    }

    private string Resolve(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.StartsWith('/') || key.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(key))
        {
            throw new ArgumentException("Storage key must be a relative path without '..'.", nameof(key));
        }

        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new ArgumentException("Storage key escapes the storage root.", nameof(key));
        }

        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
