using System.ComponentModel.DataAnnotations;

namespace TransactionImport.Infrastructure.Storage;

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary><c>local</c>, <c>s3</c>, or <c>cloudinary</c>.</summary>
    [Required]
    public string Provider { get; set; } = "local";

    /// <summary>Used when <see cref="Provider"/> is <c>local</c>.</summary>
    public string LocalRoot { get; set; } = "/data/uploads";

    // --- S3 / MinIO ---
    public string? Bucket { get; set; }

    public string? ServiceUrl { get; set; }

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    public bool ForcePathStyle { get; set; } = true;

    // --- Cloudinary (free tier works; use resource type "raw" for NDJSON) ---
    public string? CloudName { get; set; }

    public string? ApiKey { get; set; }

    public string? ApiSecret { get; set; }

    /// <summary>
    /// Optional Cloudinary folder prefix (e.g. <c>transaction-import</c>). Keys are stored under
    /// <c>{CloudinaryFolder}/{key}</c>. Raw public IDs keep the <c>.ndjson</c> extension.
    /// </summary>
    public string? CloudinaryFolder { get; set; } = "transaction-import";
}
