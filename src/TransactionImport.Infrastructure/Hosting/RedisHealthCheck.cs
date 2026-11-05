using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace TransactionImport.Infrastructure.Hosting;

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"pong in {latency.TotalMilliseconds:0} ms");
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Redis is unreachable.", exception);
        }
    }
}
