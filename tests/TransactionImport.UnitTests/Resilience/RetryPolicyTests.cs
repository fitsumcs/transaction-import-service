using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TransactionImport.Application.Abstractions;
using TransactionImport.Application.Errors;
using TransactionImport.Domain.Transactions;
using TransactionImport.Infrastructure.Resilience;

namespace TransactionImport.UnitTests.Resilience;

public sealed class RetryPolicyTests
{
    [Fact]
    public async Task Retries_transient_failures_until_success()
    {
        var metrics = new FakeMetrics();
        var policy = CreatePolicy(metrics, maxAttempts: 5, baseDelay: TimeSpan.Zero);
        var attempts = 0;

        var result = await policy.ExecuteAsync(
            "unit-op",
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new TimeoutException("flaky");
                }

                return Task.FromResult(42);
            },
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, metrics.RetryCount);
    }

    [Fact]
    public async Task Does_not_retry_validation_or_permanent_app_errors()
    {
        var metrics = new FakeMetrics();
        var policy = CreatePolicy(metrics, maxAttempts: 5, baseDelay: TimeSpan.Zero);
        var attempts = 0;

        await Assert.ThrowsAsync<AppException>(() => policy.ExecuteAsync<int>(
            "unit-op",
            _ =>
            {
                attempts++;
                throw new AppException("IMPORT_NOT_FOUND", "missing", ErrorKind.NotFound);
            },
            CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Equal(0, metrics.RetryCount);
    }

    [Fact]
    public async Task Stops_after_max_attempts_for_transient_errors()
    {
        var metrics = new FakeMetrics();
        var policy = CreatePolicy(metrics, maxAttempts: 3, baseDelay: TimeSpan.Zero);
        var attempts = 0;

        await Assert.ThrowsAsync<TimeoutException>(() => policy.ExecuteAsync<int>(
            "unit-op",
            _ =>
            {
                attempts++;
                throw new TimeoutException("still flaky");
            },
            CancellationToken.None));

        Assert.Equal(3, attempts);
        Assert.Equal(2, metrics.RetryCount);
    }

    [Fact]
    public void Classifier_marks_timeouts_transient_and_cancellation_permanent()
    {
        var classifier = new TransientErrorClassifier();
        Assert.True(classifier.IsTransient(new TimeoutException()));
        Assert.True(classifier.IsTransient(new AppException("X", "y", ErrorKind.Unavailable, retryable: true)));
        Assert.False(classifier.IsTransient(new OperationCanceledException()));
        Assert.False(classifier.IsTransient(new AppException("X", "y", ErrorKind.RequestValidation)));
        Assert.False(classifier.IsTransient(new InvalidOperationException("bug")));
    }

    private static ExponentialBackoffRetryPolicy CreatePolicy(FakeMetrics metrics, int maxAttempts, TimeSpan baseDelay) =>
        new(
            new TransientErrorClassifier(),
            metrics,
            new FakeTimeProvider(),
            Options.Create(new RetryOptions
            {
                MaxAttempts = maxAttempts,
                BaseDelay = baseDelay,
                MaxDelay = TimeSpan.FromSeconds(1),
            }),
            NullLogger<ExponentialBackoffRetryPolicy>.Instance);

    private sealed class FakeMetrics : IImportMetrics
    {
        public int RetryCount { get; private set; }

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

        public void Retry(string operation) => RetryCount++;

        public void UploadStarted()
        {
        }

        public void UploadFinished(string outcome, long bytes)
        {
        }
    }
}
