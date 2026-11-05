using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactionImport.Worker;

namespace TransactionImport.IntegrationTests.Infrastructure;

/// <summary>Hosts the Worker process (outbox relay + import consumer + stale recovery) against testcontainers.</summary>
public sealed class WorkerFactory : WebApplicationFactory<WorkerAssemblyMarker>
{
    private readonly string _connectionString;
    private readonly string _redisConnectionString;
    private readonly string _storageRoot;
    private readonly string _streamKey;
    private readonly string _dlqKey;
    private readonly string _consumerGroup;

    public WorkerFactory(PostgresFixture fixture, string sharedStorageRoot, string queueSuffix)
        : this(fixture.ConnectionString, fixture.RedisConnectionString, sharedStorageRoot, queueSuffix)
    {
    }

    public WorkerFactory(string connectionString, string redisConnectionString, string sharedStorageRoot, string queueSuffix)
    {
        _connectionString = connectionString;
        _redisConnectionString = redisConnectionString;
        _storageRoot = sharedStorageRoot;
        _streamKey = $"imports:jobs:{queueSuffix}";
        _dlqKey = $"imports:jobs:dlq:{queueSuffix}";
        _consumerGroup = $"workers-{queueSuffix}";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Postgres:ConnectionString", _connectionString);
        builder.UseSetting("Redis:ConnectionString", _redisConnectionString);
        builder.UseSetting("Redis:StreamKey", _streamKey);
        builder.UseSetting("Redis:DeadLetterStreamKey", _dlqKey);
        builder.UseSetting("Redis:ConsumerGroup", _consumerGroup);
        builder.UseSetting("FileStorage:Provider", "local");
        builder.UseSetting("FileStorage:LocalRoot", _storageRoot);
        builder.UseSetting("RiskScoring:WorkFactor", "1");
        builder.UseSetting("Processing:BatchSize", "100");
        builder.UseSetting("Processing:MaxConcurrentImports", "2");
    }
}
