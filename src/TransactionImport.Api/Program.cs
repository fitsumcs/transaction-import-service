using TransactionImport.Api.Http;
using TransactionImport.Infrastructure;
using TransactionImport.Infrastructure.Hosting;

// Composition root: the only place where concrete implementations are bound to abstractions.
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // Request body size is enforced by UploadInspectionStream; keep Kestrel's limit aligned.
    var maxBytes = builder.Configuration.GetValue("Imports:MaxFileBytes", 256L * 1024 * 1024);
    options.Limits.MaxRequestBodySize = maxBytes + (1024 * 1024);
});

builder.Services.AddInfrastructure(builder.Configuration, includeWorkerServices: false);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseMiddleware<RequestIdMiddleware>();
app.UseExceptionHandler();

app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Transaction Import API v1");
    options.RoutePrefix = "swagger";
});

app.MapHealthEndpoints();
app.MapPrometheusScrapingEndpoint("/metrics");
app.MapImportEndpoints();

await app.RunAsync();

public partial class Program;
