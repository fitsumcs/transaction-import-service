using Amazon.S3;
using Amazon.S3.Model;
using TransactionImport.Application.Abstractions;

namespace TransactionImport.Infrastructure.Storage;

/// <summary>S3-compatible object storage (AWS S3, MinIO, ...). Uses multipart upload for large streams.</summary>
internal sealed class S3FileStorage(IAmazonS3 client, FileStorageOptions options) : IFileStorage
{
    private readonly string _bucket = options.Bucket ?? throw new InvalidOperationException("FileStorage:Bucket is required for the s3 provider.");

    public async Task WriteAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        // TransferUtility would also work; PutObject with InputStream streams without buffering the body.
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            DisablePayloadSigning = true,
        };
        await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(string key, long offset, CancellationToken cancellationToken)
    {
        try
        {
            var request = new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key,
            };
            if (offset > 0)
            {
                request.ByteRange = new ByteRange(offset, long.MaxValue);
            }

            var response = await client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new StorageObjectNotFoundException(key);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    public async Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var items = new List<StoredObject>();
        string? token = null;
        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token },
                cancellationToken).ConfigureAwait(false);
            items.AddRange(response.S3Objects.Select(obj => new StoredObject(obj.Key, obj.Size ?? 0, obj.LastModified ?? DateTime.UnixEpoch)));
            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (token is not null);

        items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return items;
    }
}
