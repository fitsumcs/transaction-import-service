using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TransactionImport.Infrastructure;
using TransactionImport.Infrastructure.Persistence.Migrations;

// One-shot process: applies pending schema migrations and exits (non-zero on failure).
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPostgres(builder.Configuration);
builder.Services.AddSingleton<MigrationRunner>();

using var host = builder.Build();
using var shutdown = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await host.Services.GetRequiredService<MigrationRunner>().RunAsync(shutdown.Token);
