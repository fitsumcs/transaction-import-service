using TransactionImport.Api.Http;
using TransactionImport.Infrastructure;
using TransactionImport.Infrastructure.Hosting;

// Composition root: the only place where concrete implementations are bound to abstractions.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
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

await app.RunAsync();
