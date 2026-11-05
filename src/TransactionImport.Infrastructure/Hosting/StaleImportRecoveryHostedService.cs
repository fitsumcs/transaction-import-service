using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionImport.Application.Processing;
using TransactionImport.Infrastructure.Queue;

namespace TransactionImport.Infrastructure.Hosting;

/// <summary>
/// Periodically recovers imports whose worker lease expired and re-enqueues pending imports whose
/// queue notification may have been lost. Complements Redis XAUTOCLAIM: the database is the source of truth.
/// </summary>
internal sealed partial class StaleImportRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<RedisOptions> redisOptions,
    ILogger<StaleImportRecoveryHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IImportProcessingStore>();
                var recovered = await store.RecoverStaleAsync(redisOptions.Value.ClaimIdleTime, stoppingToken).ConfigureAwait(false);
                if (recovered > 0)
                {
                    LogRecovered(logger, recovered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovered {Count} stale or lost-notification imports")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stale-import recovery failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
