using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace TransactionImport.Infrastructure.Hosting;

/// <summary>Reports not-ready as soon as shutdown begins so load balancers stop routing new traffic.</summary>
internal sealed class ShutdownReadinessCheck(IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            lifetime.ApplicationStopping.IsCancellationRequested
                ? HealthCheckResult.Unhealthy("Service is shutting down.")
                : HealthCheckResult.Healthy());
}
