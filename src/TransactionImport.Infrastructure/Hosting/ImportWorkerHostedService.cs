using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionImport.Application.Abstractions;
using TransactionImport.Application.Processing;
using TransactionImport.Infrastructure.Queue;

namespace TransactionImport.Infrastructure.Hosting;

/// <summary>
/// Pulls import-job notifications from the queue and processes them with bounded concurrency.
/// Ownership is decided by the database lease, so duplicate deliveries are acknowledged and skipped.
/// Poison messages (unparseable, or imports that keep failing past the queue delivery budget) go to the DLQ.
/// </summary>
internal sealed partial class ImportWorkerHostedService(
    IServiceScopeFactory scopeFactory,
    IImportJobQueue queue,
    IOptions<ProcessingOptions> processingOptions,
    IOptions<RedisOptions> redisOptions,
    ILogger<ImportWorkerHostedService> logger) : BackgroundService
{
    private readonly ProcessingOptions _processing = processingOptions.Value;
    private readonly RedisOptions _redis = redisOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, _processing.MaxConcurrentImports);
        using var gate = new SemaphoreSlim(_processing.MaxConcurrentImports, _processing.MaxConcurrentImports);
        var inFlight = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            inFlight.RemoveAll(task => task.IsCompleted);

            try
            {
                await gate.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            IReadOnlyList<ReceivedJob> jobs;
            try
            {
                jobs = await queue.ReceiveAsync(1, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                gate.Release();
                break;
            }
            catch (Exception exception)
            {
                gate.Release();
                LogReceiveFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (jobs.Count == 0)
            {
                gate.Release();
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false);
                continue;
            }

            var job = jobs[0];
            inFlight.Add(Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(job, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDrainFailed(logger, exception);
        }
    }

    private async Task HandleAsync(ReceivedJob job, CancellationToken stoppingToken)
    {
        if (job.ImportId is null)
        {
            await queue.DeadLetterAsync(job, "unparseable_payload", stoppingToken).ConfigureAwait(false);
            return;
        }

        using var activity = new Activity("import.process");
        if (job.TraceParent is not null)
        {
            activity.SetParentId(job.TraceParent);
        }

        activity.Start();
        activity.SetTag("import.id", job.ImportId.Value.ToString("N"));

        await using var scope = scopeFactory.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<ImportProcessor>();
        var store = scope.ServiceProvider.GetRequiredService<IImportProcessingStore>();

        ProcessingOutcome outcome;
        try
        {
            outcome = await processor.ProcessAsync(job.ImportId.Value, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogProcessFailed(logger, exception, job.ImportId.Value);
            if (job.DeliveryCount >= _redis.MaxDeliveryAttempts)
            {
                await store.FailUnleasedAsync(job.ImportId.Value, "PROCESSING_ABANDONED", "Processing failed repeatedly.", stoppingToken)
                    .ConfigureAwait(false);
                await queue.DeadLetterAsync(job, "max_deliveries", stoppingToken).ConfigureAwait(false);
            }

            return;
        }

        await queue.AcknowledgeAsync(job, CancellationToken.None).ConfigureAwait(false);
        LogOutcome(logger, job.ImportId.Value, outcome);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Import worker started (max concurrent imports: {MaxConcurrent})")]
    private static partial void LogStarted(ILogger logger, int maxConcurrent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Import {ImportId} finished with outcome {Outcome}")]
    private static partial void LogOutcome(ILogger logger, Guid importId, ProcessingOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to receive from the job queue")]
    private static partial void LogReceiveFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled processing failure for import {ImportId}")]
    private static partial void LogProcessFailed(ILogger logger, Exception exception, Guid importId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error while draining in-flight imports during shutdown")]
    private static partial void LogDrainFailed(ILogger logger, Exception exception);
}
