using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionImport.Application.Abstractions;

namespace TransactionImport.Infrastructure.Resilience;

/// <summary>
/// Bounded retries with exponential backoff and full jitter. Only transient errors are retried;
/// cancellation always aborts. Metrics and structured logs record every attempt.
/// </summary>
internal sealed partial class ExponentialBackoffRetryPolicy(
    ITransientErrorClassifier classifier,
    IImportMetrics metrics,
    TimeProvider timeProvider,
    IOptions<RetryOptions> options,
    ILogger<ExponentialBackoffRetryPolicy> logger) : IRetryPolicy
{
    private readonly RetryOptions _options = options.Value;

    public async Task<T> ExecuteAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= _options.MaxAttempts || !classifier.IsTransient(exception) || cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                metrics.Retry(operation);
                var delay = ComputeDelay(attempt);
                LogRetry(logger, operation, attempt, delay, exception.GetType().Name);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        // Full jitter: random in [0, min(max, base * 2^(attempt-1))].
        var exponential = _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(_options.MaxDelay.TotalMilliseconds, exponential);
        var jittered = Random.Shared.NextDouble() * capped;
        return TimeSpan.FromMilliseconds(jittered);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retrying {Operation} (attempt {Attempt}, delay {Delay}) after {ErrorType}")]
    private static partial void LogRetry(ILogger logger, string operation, int attempt, TimeSpan delay, string errorType);
}
