using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactionImport.Api;

namespace TransactionImport.IntegrationTests.Infrastructure;

public sealed class ApiFactory : WebApplicationFactory<ApiAssemblyMarker>
{
    public const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";
    public const string UnreachableRedis = "127.0.0.1:1,connectTimeout=500,syncTimeout=500";

    private readonly string _connectionString;
    private readonly string _redisConnectionString;
    private readonly string _storageRoot;
    private readonly string _streamKey;
    private readonly string _dlqKey;
    private readonly string _consumerGroup;

    public ApiFactory(PostgresFixture fixture, string? sharedStorageRoot = null, string? queueSuffix = null)
        : this(fixture.ConnectionString, fixture.RedisConnectionString, sharedStorageRoot, queueSuffix)
    {
    }

    public ApiFactory(
        string connectionString,
        string redisConnectionString,
        string? sharedStorageRoot = null,
        string? queueSuffix = null)
    {
        _connectionString = connectionString;
        _redisConnectionString = redisConnectionString;
        _storageRoot = sharedStorageRoot
            ?? Path.Combine(Path.GetTempPath(), "transaction-import-tests", Guid.NewGuid().ToString("N"));
        var suffix = queueSuffix ?? Guid.NewGuid().ToString("N");
        _streamKey = $"imports:jobs:{suffix}";
        _dlqKey = $"imports:jobs:dlq:{suffix}";
        _consumerGroup = $"workers-{suffix}";
    }

    public string StorageRoot => _storageRoot;

    public string QueueSuffix => _streamKey["imports:jobs:".Length..];

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
