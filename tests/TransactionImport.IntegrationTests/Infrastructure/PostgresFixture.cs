using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(TransactionImport.IntegrationTests.Infrastructure.PostgresFixture))]

namespace TransactionImport.IntegrationTests.Infrastructure;

/// <summary>One disposable PostgreSQL container shared by the whole test assembly.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("transaction_import_tests")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
