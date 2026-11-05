using TransactionImport.Infrastructure;
using TransactionImport.Infrastructure.Hosting;

// Composition root for the processing host. It exposes HTTP only for health and metrics;
// import traffic is served exclusively by the API process.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapHealthEndpoints();

await app.RunAsync();
