namespace TransactionImport.Application.Imports;

public sealed class ImportOptions
{
    public const string SectionName = "Imports";

    public long MaxFileBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>How long an in-progress idempotency key blocks other requests; must exceed the slowest expected upload.</summary>
    public TimeSpan IdempotencyLockDuration { get; set; } = TimeSpan.FromMinutes(15);

    public int MaxProcessingAttempts { get; set; } = 3;

    public int MaxRejectionPageSize { get; set; } = 200;

    public int SummaryTopGroups { get; set; } = 20;
}

public static class StorageKeys
{
    public static string ForImport(Guid importId, DateTimeOffset now) =>
        $"imports/{now:yyyy}/{now:MM}/{now:dd}/{importId:N}.ndjson";

    public static string UploadChunkPrefix(Guid uploadId) => $"uploads/{uploadId:N}/";

    public static string UploadChunk(Guid uploadId, long offset) => $"uploads/{uploadId:N}/{offset:D20}.part";

    public static bool IsChunked(string storageKey) => storageKey.EndsWith('/');
}
