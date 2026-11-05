using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using TransactionImport.Application.Abstractions;
using TransactionImport.Infrastructure.Hosting;
using TransactionImport.Infrastructure.Identity;
using TransactionImport.Infrastructure.Persistence;

namespace TransactionImport.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPostgres(configuration);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, GuidV7IdGenerator>();

        services.AddHealthChecks()
            .AddCheck<ShutdownReadinessCheck>("shutdown", tags: [HealthEndpoints.ReadyTag])
            .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(3));

        return services;
    }

    public static IServiceCollection AddPostgres(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PostgresOptions>()
            .Bind(configuration.GetSection(PostgresOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PostgresOptions>>().Value;
            return new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
        });

        return services;
    }
}
