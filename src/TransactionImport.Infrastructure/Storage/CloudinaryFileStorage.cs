using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Logging;
using TransactionImport.Application.Abstractions;

namespace TransactionImport.Infrastructure.Storage;

/// <summary>
/// Cloudinary adapter for <see cref="IFileStorage"/>. NDJSON files are stored as <c>raw</c> assets
/// (not images). Suitable for the free tier; for very large production imports prefer S3.
/// </summary>
internal sealed partial class CloudinaryFileStorage : IFileStorage
{
    public const string HttpClientName = "cloudinary-download";

    private readonly Cloudinary _cloudinary;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _folder;
    private readonly ILogger<CloudinaryFileStorage> _logger;

    public CloudinaryFileStorage(FileStorageOptions options, IHttpClientFactory httpClientFactory, ILogger<CloudinaryFileStorage> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CloudName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiSecret);

        _cloudinary = new Cloudinary(new Account(options.CloudName, options.ApiKey, options.ApiSecret))
        {
            Api = { Secure = true },
        };
        _httpClientFactory = httpClientFactory;
        _folder = NormalizeFolder(options.CloudinaryFolder);
        _logger = logger;
    }

    public async Task WriteAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        var publicId = ToPublicId(key);
        var fileName = Path.GetFileName(key);
        if (string.IsNullOrEmpty(fileName))
        {
            fileName = "upload.ndjson";
        }

        // FileDescription reads the stream in chunks; do not dispose the caller's stream.
        var upload = new RawUploadParams
        {
            File = new FileDescription(fileName, content),
            PublicId = publicId,
            Overwrite = true,
            Invalidate = true,
            UseFilename = false,
            UniqueFilename = false,
        };

        var result = await _cloudinary.UploadAsync(upload, "raw", cancellationToken).ConfigureAwait(false);
        if (result.Error is not null || result.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.Ambiguous)
        {
            throw new InvalidOperationException(
                $"Cloudinary upload failed for '{key}': {result.Error?.Message ?? result.StatusCode.ToString()}");
        }

        LogUploaded(_logger, publicId, result.Bytes);
    }

    public async Task<Stream> OpenReadAsync(string key, long offset, CancellationToken cancellationToken)
    {
        var publicId = ToPublicId(key);
        var resource = await _cloudinary.GetResourceAsync(
            new GetResourceParams(publicId) { ResourceType = ResourceType.Raw },
            cancellationToken).ConfigureAwait(false);

        if (resource.Error is not null || string.IsNullOrWhiteSpace(resource.SecureUrl))
        {
            throw new StorageObjectNotFoundException(key);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, resource.SecureUrl);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        // Caller owns the response stream; do not dispose the response until the stream is disposed.
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new StorageObjectNotFoundException(key);
        }

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new InvalidOperationException(
                $"Cloudinary download failed for '{key}': {(int)response.StatusCode} {body}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResponseStream(stream, response);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var publicId = ToPublicId(key);

        // CloudinaryDotNet DestroyAsync has no CancellationToken overload.
        var result = await _cloudinary.DestroyAsync(
            new DeletionParams(publicId)
            {
                ResourceType = ResourceType.Raw,
                Invalidate = true,
            }).ConfigureAwait(false);

        // "not found" is treated as success for idempotent cleanup.
        if (result.Error is not null
            && !string.Equals(result.Result, "not found", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(result.Result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            LogDeleteFailed(_logger, publicId, result.Error.Message ?? result.Result ?? "unknown");
        }
    }

    public async Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var cloudPrefix = ToPublicId(prefix.TrimEnd('/'));
        var items = new List<StoredObject>();
        string? nextCursor = null;

        do
        {
            var result = await _cloudinary.ListResourcesAsync(
                new ListResourcesByPrefixParams
                {
                    Prefix = cloudPrefix,
                    ResourceType = ResourceType.Raw,
                    MaxResults = 500,
                    NextCursor = nextCursor,
                },
                cancellationToken).ConfigureAwait(false);

            if (result.Error is not null)
            {
                throw new InvalidOperationException($"Cloudinary list failed for '{prefix}': {result.Error.Message}");
            }

            if (result.Resources is not null)
            {
                foreach (var resource in result.Resources)
                {
                    items.Add(new StoredObject(
                        FromPublicId(resource.PublicId),
                        resource.Bytes,
                        ParseCreatedAt(resource.CreatedAt)));
                }
            }

            nextCursor = result.NextCursor;
        }
        while (!string.IsNullOrEmpty(nextCursor));

        items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return items;
    }

    internal string ToPublicId(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Storage key must not contain '..'.", nameof(key));
        }

        return string.IsNullOrEmpty(_folder) ? normalized : $"{_folder}/{normalized}";
    }

    internal string FromPublicId(string publicId)
    {
        if (!string.IsNullOrEmpty(_folder)
            && publicId.StartsWith(_folder + "/", StringComparison.Ordinal))
        {
            return publicId[(_folder.Length + 1)..];
        }

        return publicId;
    }

    private static string? NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        return folder.Replace('\\', '/').Trim('/');
    }

    private static DateTimeOffset ParseCreatedAt(string? created)
    {
        if (!string.IsNullOrWhiteSpace(created)
            && DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return DateTimeOffset.UtcNow;
    }

    /// <summary>Keeps the HTTP response alive until the body stream is disposed.</summary>
    private sealed class HttpResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Uploaded raw asset {PublicId} ({Bytes} bytes) to Cloudinary")]
    private static partial void LogUploaded(ILogger logger, string publicId, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cloudinary delete of {PublicId} returned: {Message}")]
    private static partial void LogDeleteFailed(ILogger logger, string publicId, string message);
}
