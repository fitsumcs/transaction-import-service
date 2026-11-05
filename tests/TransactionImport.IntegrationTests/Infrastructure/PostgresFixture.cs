using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using TransactionImport.Infrastructure.Persistence.Migrations;

[assembly: AssemblyFixture(typeof(TransactionImport.IntegrationTests.Infrastructure.PostgresFixture))]

namespace TransactionImport.IntegrationTests.Infrastructure;

/// <summary>
/// Shared PostgreSQL + Redis containers for the test assembly, migrated with the production runner.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("transaction_import_tests")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await new MigrationRunner(DataSource, NullLogger<MigrationRunner>.Instance).RunAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
}
