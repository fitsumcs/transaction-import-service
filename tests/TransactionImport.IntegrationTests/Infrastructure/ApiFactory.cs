using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TransactionImport.IntegrationTests.Infrastructure;

public sealed class ApiFactory(string connectionString, string redisConnectionString) : WebApplicationFactory<Program>
{
    public const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";
    public const string UnreachableRedis = "127.0.0.1:1,connectTimeout=500,syncTimeout=500";

    private readonly string _connectionString = connectionString;
    private readonly string _redisConnectionString = redisConnectionString;

    public ApiFactory(PostgresFixture fixture)
        : this(fixture.ConnectionString, fixture.RedisConnectionString)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Postgres:ConnectionString", _connectionString);
        builder.UseSetting("Redis:ConnectionString", _redisConnectionString);
        builder.UseSetting("FileStorage:Provider", "local");
        builder.UseSetting("FileStorage:LocalRoot", Path.Combine(Path.GetTempPath(), "transaction-import-tests", Guid.NewGuid().ToString("N")));
    }
}
