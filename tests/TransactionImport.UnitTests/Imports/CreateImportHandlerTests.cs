using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TransactionImport.Application.Abstractions;
using TransactionImport.Application.Errors;
using TransactionImport.Application.Imports;
using TransactionImport.Application.Processing;
using TransactionImport.Domain.Imports;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.UnitTests.Imports;

/// <summary>
/// Exercises CreateImportHandler with in-memory fakes — the required demonstration that production
/// dependencies can be replaced without changing the use case.
/// </summary>
public sealed class CreateImportHandlerTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeIds _ids = new();
    private readonly FakeStorage _storage = new();
    private readonly FakeRepository _repository = new();
    private readonly FakeMetrics _metrics = new();

    [Fact]
    public async Task Creates_an_import_from_a_streamed_upload()
    {
        var handler = CreateHandler();
        var content = "{\"transactionId\":\"t\"}\n"u8.ToArray();

        var result = await handler.HandleAsync(
            new CreateImportCommand("provider-a", "key-1", "../evil/file.ndjson", new MemoryStream(content), null),
            TestContext.Current.CancellationToken);

        Assert.False(result.Replayed);
        Assert.Equal("file.ndjson", result.Import.FileName);
        Assert.Equal(content.Length, result.Import.FileSizeBytes);
        Assert.Equal(ImportStatus.Pending, result.Import.Status);
        Assert.Single(_storage.Objects);
        Assert.Equal(1, _metrics.UploadsAccepted);
        Assert.True(_repository.Created);
    }

    [Fact]
    public async Task Replays_a_completed_idempotency_key_without_uploading_again()
    {
        var existingId = Guid.CreateVersion7();
        _repository.Reservation = new IdempotencyReservation(IdempotencyReservationKind.Replay, existingId);
        _repository.Existing = View(existingId);
        var handler = CreateHandler();

        var result = await handler.HandleAsync(
            new CreateImportCommand("provider-a", "key-1", "f.ndjson", new MemoryStream("{"u8.ToArray()), null),
            TestContext.Current.CancellationToken);

        Assert.True(result.Replayed);
        Assert.Empty(_storage.Objects);
        Assert.False(_repository.Created);
    }

    [Fact]
    public async Task Concurrent_in_progress_key_is_rejected()
    {
        _repository.Reservation = new IdempotencyReservation(IdempotencyReservationKind.InProgress, null);
        var handler = CreateHandler();

        var error = await Assert.ThrowsAsync<AppException>(() => handler.HandleAsync(
            new CreateImportCommand("provider-a", "key-1", "f.ndjson", new MemoryStream("{"u8.ToArray()), null),
            TestContext.Current.CancellationToken));

        Assert.Equal("IDEMPOTENCY_KEY_IN_PROGRESS", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task Failed_upload_deletes_the_partial_object_and_releases_the_key()
    {
        _storage.FailWrite = true;
        var handler = CreateHandler();

        await Assert.ThrowsAsync<IOException>(() => handler.HandleAsync(
            new CreateImportCommand("provider-a", "key-1", "f.ndjson", new MemoryStream("{"u8.ToArray()), null),
            TestContext.Current.CancellationToken));

        Assert.True(_repository.Released);
        Assert.Empty(_storage.Objects);
        Assert.Equal(1, _metrics.UploadsFailed);
    }

    private CreateImportHandler CreateHandler() => new(
        _repository,
        _storage,
        _ids,
        _clock,
        _metrics,
        Options.Create(new ImportOptions { MaxFileBytes = 1024, MaxProcessingAttempts = 3 }),
        NullLogger<CreateImportHandler>.Instance);

    private static ImportView View(Guid id) => new(
        id, "provider-a", ImportStatus.Pending, "f.ndjson", 10, ImportProgress.Empty, 0,
        DateTimeOffset.UtcNow, null, null, null, null);

    private sealed class FakeIds : IIdGenerator
    {
        private int _n;
        public Guid NewId() => Guid.Parse($"00000000-0000-7000-0000-{++_n:D12}");
    }

    private sealed class FakeStorage : IFileStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public bool FailWrite { get; set; }

        public async Task WriteAsync(string key, Stream content, CancellationToken cancellationToken)
        {
            if (FailWrite)
            {
                // Consume a few bytes so the inspection stream starts, then fail.
                _ = await content.ReadAsync(new byte[4], cancellationToken);
                throw new IOException("disk full");
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Objects[key] = buffer.ToArray();
        }

        public Task<Stream> OpenReadAsync(string key, long offset, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(Objects[key][(int)offset..]));

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredObject>>(
                Objects.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(pair => new StoredObject(pair.Key, pair.Value.Length, DateTimeOffset.UtcNow))
                    .ToArray());
    }

    private sealed class FakeRepository : IImportRepository
    {
        public IdempotencyReservation Reservation { get; set; } =
            new(IdempotencyReservationKind.Reserved, null);

        public ImportView? Existing { get; set; }
        public bool Created { get; private set; }
        public bool Released { get; private set; }

        public Task<IdempotencyReservation> ReserveIdempotencyKeyAsync(string providerId, string key, Guid lockToken, TimeSpan lockDuration, CancellationToken cancellationToken) =>
            Task.FromResult(Reservation);

        public Task ReleaseIdempotencyKeyAsync(string providerId, string key, Guid lockToken, CancellationToken cancellationToken)
        {
            Released = true;
            return Task.CompletedTask;
        }

        public Task<ImportView?> CreateAsync(NewImport import, string idempotencyKey, Guid lockToken, string? traceParent, CancellationToken cancellationToken)
        {
            Created = true;
            return Task.FromResult<ImportView?>(View(import.Id) with
            {
                FileName = import.FileName,
                FileSizeBytes = import.FileSizeBytes,
            });
        }

        public Task<ImportView?> GetAsync(Guid importId, ProviderScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(Existing);

        public Task<CancelResult> RequestCancellationAsync(Guid importId, ProviderScope scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RetryResult> RetryAsync(Guid importId, ProviderScope scope, string? traceParent, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ImportSummary?> GetSummaryAsync(Guid importId, ProviderScope scope, int topGroups, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RecordRejection>?> GetRejectionsAsync(Guid importId, ProviderScope scope, long afterLine, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMetrics : IImportMetrics
    {
        public int UploadsAccepted { get; private set; }
        public int UploadsFailed { get; private set; }

        public void ImportStarted()
        {
        }

        public void ImportFinished(string outcome, TimeSpan duration)
        {
        }

        public void RecordsCommitted(long accepted, long rejected, long duplicates)
        {
        }

        public void RecordRejected(RejectionCode code)
        {
        }

        public void BatchStage(string stage, TimeSpan duration)
        {
        }

        public void ProcessingFailure(string kind)
        {
        }

        public void Retry(string operation)
        {
        }

        public void UploadStarted()
        {
        }

        public void UploadFinished(string outcome, long bytes)
        {
            if (outcome == "accepted")
            {
                UploadsAccepted++;
            }
            else
            {
                UploadsFailed++;
            }
        }
    }
}
