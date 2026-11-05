using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using TransactionImport.Application.Abstractions;
using TransactionImport.Application.Imports;
using TransactionImport.Application.Processing;
using TransactionImport.Infrastructure.Hosting;
using TransactionImport.Infrastructure.Identity;
using TransactionImport.Infrastructure.Observability;
using TransactionImport.Infrastructure.Persistence;
using TransactionImport.Infrastructure.Queue;
using TransactionImport.Infrastructure.Resilience;
using TransactionImport.Infrastructure.Scoring;
using TransactionImport.Infrastructure.Storage;

namespace TransactionImport.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, bool includeWorkerServices = false)
    {
        services.AddPostgres(configuration);
        services.AddRedis(configuration);
        services.AddFileStorage(configuration);
        services.AddScoring(configuration);
        services.AddResilience(configuration);
        services.AddApplicationServices(configuration);
        services.AddObservability(configuration);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, GuidV7IdGenerator>();
        services.AddSingleton<IWorkerIdentity, WorkerIdentity>();
        services.AddSingleton<IImportJobQueue, RedisImportJobQueue>();
        services.AddSingleton<ImportFileReader>();
        services.AddSingleton(provider => new TransactionLineParser(provider.GetRequiredService<IOptions<ValidationOptions>>().Value));

        services.AddScoped<IImportRepository, PostgresImportRepository>();
        services.AddScoped<IImportProcessingStore, PostgresImportProcessingStore>();
        services.AddScoped<CreateImportHandler>();
        services.AddScoped<ImportQueries>();
        services.AddScoped<ImportCommands>();
        services.AddScoped<ImportProcessor>();

        services.AddHealthChecks()
            .AddCheck<ShutdownReadinessCheck>("shutdown", tags: [HealthEndpoints.ReadyTag])
            .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(3))
            .AddCheck<RedisHealthCheck>("redis", tags: [HealthEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(3));

        if (includeWorkerServices)
        {
            services.AddHostedService<OutboxRelayHostedService>();
            services.AddHostedService<ImportWorkerHostedService>();
            services.AddHostedService<StaleImportRecoveryHostedService>();
            services.AddHostedService(provider => (BoundedRiskScoringPool)provider.GetRequiredService<IRiskScoringPool>());
        }

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
            var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);
            builder.UseLoggerFactory(provider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>());
            return builder.Build();
        });

        return services;
    }

    private static IServiceCollection AddRedis(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<RedisOptions>>().Value;
            return ConnectionMultiplexer.Connect(options.ConnectionString);
        });

        return services;
    }

    private static IServiceCollection AddFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(CloudinaryFileStorage.HttpClientName);

        services.AddSingleton<IFileStorage>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<FileStorageOptions>>().Value;
            return options.Provider.ToLowerInvariant() switch
            {
                "local" => new LocalFileStorage(options),
                "s3" => new S3FileStorage(CreateS3Client(options), options),
                "cloudinary" => new CloudinaryFileStorage(
                    options,
                    provider.GetRequiredService<IHttpClientFactory>(),
                    provider.GetRequiredService<ILogger<CloudinaryFileStorage>>()),
                _ => throw new InvalidOperationException($"Unknown FileStorage:Provider '{options.Provider}'."),
            };
        });

        return services;
    }

    private static AmazonS3Client CreateS3Client(FileStorageOptions options)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }

        if (!string.IsNullOrWhiteSpace(options.AccessKey) && !string.IsNullOrWhiteSpace(options.SecretKey))
        {
            return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        }

        return new AmazonS3Client(config);
    }

    private static IServiceCollection AddScoring(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RiskScoringOptions>()
            .Bind(configuration.GetSection(RiskScoringOptions.SectionName));

        services.AddSingleton<IRiskScorer, LocalRiskScorer>();
        services.AddSingleton<IRiskScoringPool, BoundedRiskScoringPool>();
        return services;
    }

    private static IServiceCollection AddResilience(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RetryOptions>()
            .Bind(configuration.GetSection(RetryOptions.SectionName));

        services.AddSingleton<ITransientErrorClassifier, TransientErrorClassifier>();
        services.AddSingleton<IRetryPolicy, ExponentialBackoffRetryPolicy>();
        return services;
    }

    private static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ImportOptions>()
            .Bind(configuration.GetSection(ImportOptions.SectionName));
        services.AddOptions<ProcessingOptions>()
            .Bind(configuration.GetSection(ProcessingOptions.SectionName));
        services.AddOptions<ValidationOptions>()
            .Bind(configuration.GetSection(ValidationOptions.SectionName));
        return services;
    }

    private static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IImportMetrics, OpenTelemetryImportMetrics>();

        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("transaction-import"))
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(OpenTelemetryImportMetrics.MeterName)
                    .AddRuntimeInstrumentation()
                    .AddPrometheusExporter();
            })
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddSource("TransactionImport");
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter();
                }
            });

        return services;
    }
}
