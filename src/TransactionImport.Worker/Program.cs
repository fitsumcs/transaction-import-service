using TransactionImport.Infrastructure;
using TransactionImport.Infrastructure.Hosting;

// Composition root for the processing host. HTTP is only for health and metrics;
// import traffic is served exclusively by the API process.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration, includeWorkerServices: true);

var app = builder.Build();

app.MapHealthEndpoints();
app.MapPrometheusScrapingEndpoint("/metrics");

await app.RunAsync();
