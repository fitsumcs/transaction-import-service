using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using TransactionImport.Infrastructure.Persistence.Migrations;

[assembly: AssemblyFixture(typeof(TransactionImport.IntegrationTests.Infrastructure.PostgresFixture))]

namespace TransactionImport.IntegrationTests.Infrastructure;

/// <summary>
/// One disposable PostgreSQL container for the whole test assembly, migrated with the production
/// migration runner. Tests isolate themselves with unique ids instead of truncating tables.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("transaction_import_tests")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await new MigrationRunner(DataSource, NullLogger<MigrationRunner>.Instance).RunAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}
