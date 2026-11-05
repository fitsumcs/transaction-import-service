using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TransactionImport.IntegrationTests.Infrastructure;

public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Postgres:ConnectionString", connectionString);
    }
}
