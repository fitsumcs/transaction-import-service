using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionImport.Application.Abstractions;
using TransactionImport.Application.Errors;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.Application.Processing;

/// <summary>
/// Processes one import: claim → stream/parse → score → commit → finish.
/// <list type="bullet">
/// <item>The file is never buffered whole; the reader and a small channel of prepared batches apply backpressure.</item>
/// <item>Each batch is one database transaction (rows + rejections + progress + checkpoint + lease renewal).</item>
/// <item>Scoring runs through a shared, bounded pool so CPU-heavy work cannot starve other imports.</item>
/// <item>Shutdown finishes the in-flight batch then releases the job; cancellation confirms without further writes;
/// lease loss stops immediately.</item>
/// </list>
/// </summary>
public sealed partial class ImportProcessor(
    IImportProcessingStore store,
    ImportFileReader fileReader,
    TransactionLineParser parser,
    IRiskScoringPool scoringPool,
    IWorkerIdentity worker,
    IIdGenerator ids,
    IRetryPolicy retries,
    IImportMetrics metrics,
    TimeProvider timeProvider,
    IOptions<ProcessingOptions> options,
    ILogger<ImportProcessor> logger)
{
    private readonly ProcessingOptions _options = options.Value;

    public async Task<ProcessingOutcome> ProcessAsync(Guid importId, CancellationToken shutdownToken)
    {
        var leaseToken = ids.NewId();
        ClaimResult claim;
        try
        {
            claim = await retries.ExecuteAsync(
                    "claim-import",
                    ct => store.TryClaimAsync(importId, worker.WorkerId, leaseToken, _options.LeaseDuration, ct),
                    shutdownToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metrics.ProcessingFailure("claim");
            LogClaimFailed(logger, exception, importId);
            return ProcessingOutcome.Released;
        }

        switch (claim.Status)
        {
            case ClaimStatus.NotFound:
                return ProcessingOutcome.NotFound;
            case ClaimStatus.NotClaimable:
                return ProcessingOutcome.Skipped;
            case ClaimStatus.CancelConfirmed:
                metrics.ImportFinished("cancelled", TimeSpan.Zero);
                return ProcessingOutcome.Cancelled;
            case ClaimStatus.AttemptsExhausted:
                metrics.ImportFinished("failed", TimeSpan.Zero);
                return ProcessingOutcome.Failed;
        }

        var job = claim.Job!;
        metrics.ImportStarted();
        var started = timeProvider.GetTimestamp();
        LogClaimed(logger, job.ImportId, job.Attempt, job.CheckpointLineNumber);

        using var control = new ProcessingControl(shutdownToken);
        using var heartbeat = new CancellationTokenSource();
        var heartbeatTask = HeartbeatAsync(job.ImportId, leaseToken, control, heartbeat.Token);

        ProcessingOutcome outcome;
        try
        {
            await ProcessClaimedAsync(job, leaseToken, control).ConfigureAwait(false);
            outcome = await FinalizeAsync(job.ImportId, leaseToken, control).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metrics.ProcessingFailure("process");
            LogProcessFailed(logger, exception, job.ImportId);
            outcome = await FailOrReleaseAsync(job.ImportId, leaseToken, job.Attempt, exception).ConfigureAwait(false);
        }
        finally
        {
            await heartbeat.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        metrics.ImportFinished(outcome.ToString().ToLowerInvariant(), timeProvider.GetElapsedTime(started));
        return outcome;
    }

    private async Task ProcessClaimedAsync(ImportJob job, Guid leaseToken, ProcessingControl control)
    {
        await using var stream = await fileReader.OpenAsync(job.StorageKey, job.CheckpointByteOffset, control.ReadToken).ConfigureAwait(false);
        using var reader = new NdjsonLineReader(stream, _options.MaxLineBytes, job.CheckpointByteOffset, job.CheckpointLineNumber);

        var channel = Channel.CreateBounded<PreparedBatch>(new BoundedChannelOptions(_options.PrefetchBatches)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = ProduceBatchesAsync(reader, channel.Writer, control);
        var consumer = ConsumeBatchesAsync(job, leaseToken, channel.Reader, control);

        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
    }

    private async Task ProduceBatchesAsync(NdjsonLineReader reader, ChannelWriter<PreparedBatch> writer, ProcessingControl control)
    {
        try
        {
            var builder = new BatchBuilder(reader.EndOffset, reader.LineNumber);
            var batchNumber = 0;
            var now = timeProvider.GetUtcNow();

            while (!control.ReadToken.IsCancellationRequested)
            {
                bool hasLine;
                try
                {
                    hasLine = await reader.ReadNextAsync(control.ReadToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (control.ReadToken.IsCancellationRequested)
                {
                    break;
                }

                if (!hasLine)
                {
                    break;
                }

                var stageStart = timeProvider.GetTimestamp();
                var parsed = reader.IsTooLong
                    ? TransactionLineParser.ParseTooLong(reader.LineNumber, reader.Current)
                    : parser.Parse(reader.Current, reader.LineNumber, now);
                metrics.BatchStage("parse", timeProvider.GetElapsedTime(stageStart));

                // Skipped lines only advance the checkpoint so they are never re-read.
                builder.Add(parsed, reader.LineNumber, reader.EndOffset);

                if (builder.RecordCount >= _options.BatchSize)
                {
                    await writer.WriteAsync(builder.Take(++batchNumber), control.ReadToken).ConfigureAwait(false);
                }
            }

            if (builder.RecordCount > 0 || builder.HasLines)
            {
                // Flush remaining work. On cancel/lease-loss the consumer has already stopped, so this write
                // is cancelled by ReadToken. On shutdown, ReadToken is cancelled but we still flush so the
                // in-flight batch can be committed before release: reopen the channel write with WorkToken
                // only for the final flush when shutdown is the reason.
                var flushToken = control.Reason == StopReason.None && control.ShutdownRequested
                    ? CancellationToken.None
                    : control.ReadToken;
                if (!flushToken.IsCancellationRequested || control.ShutdownRequested)
                {
                    try
                    {
                        await writer.WriteAsync(builder.Take(++batchNumber), control.ShutdownRequested ? CancellationToken.None : flushToken)
                            .ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                    }
                }
            }
        }
        catch (OperationCanceledException) when (control.ReadToken.IsCancellationRequested)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task ConsumeBatchesAsync(ImportJob job, Guid leaseToken, ChannelReader<PreparedBatch> reader, ProcessingControl control)
    {
        try
        {
            await foreach (var batch in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (control.Reason is StopReason.CancelRequested or StopReason.LeaseLost)
                {
                    break;
                }

                // On shutdown, finish this one batch then stop; do not start another.
                var result = await CommitPreparedAsync(job, leaseToken, batch, control).ConfigureAwait(false);
                switch (result.State)
                {
                    case LeaseState.CancelRequested:
                        control.RequestStop(StopReason.CancelRequested);
                        return;
                    case LeaseState.Lost:
                        control.RequestStop(StopReason.LeaseLost);
                        return;
                }

                if (control.ShutdownRequested)
                {
                    control.StopReading();
                    return;
                }
            }
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task<BatchCommitResult> CommitPreparedAsync(ImportJob job, Guid leaseToken, PreparedBatch batch, ProcessingControl control)
    {
        var stageStart = timeProvider.GetTimestamp();
        var known = batch.Candidates.Count == 0
            ? (IReadOnlyDictionary<string, byte[]>)new Dictionary<string, byte[]>()
            : await store.GetExistingFingerprintsAsync(job.ProviderId, batch.Candidates.Select(c => c.Transaction.TransactionId).ToArray(), control.WorkToken)
                .ConfigureAwait(false);
        metrics.BatchStage("lookup", timeProvider.GetElapsedTime(stageStart));

        var rejections = new List<RecordRejection>(batch.Rejections);
        var toScore = new List<PreparedCandidate>(batch.Candidates.Count);
        foreach (var candidate in batch.Candidates)
        {
            if (known.TryGetValue(candidate.Transaction.TransactionId, out var existing))
            {
                rejections.Add(DuplicateClassifier.Classify(candidate.LineNumber, candidate.Transaction.TransactionId, candidate.Fingerprint, existing));
            }
            else
            {
                toScore.Add(candidate);
            }
        }

        foreach (var rejection in batch.Rejections)
        {
            metrics.RecordRejected(rejection.Code);
        }

        RiskScore[]? scores = null;
        if (toScore.Count > 0)
        {
            stageStart = timeProvider.GetTimestamp();
            var requests = toScore.Select(c => new ScoringRequest(c.Transaction, c.Fingerprint)).ToArray();
            scores = await scoringPool.ScoreAsync(requests, control.WorkToken).ConfigureAwait(false);
            metrics.BatchStage("score", timeProvider.GetElapsedTime(stageStart));
        }

        var accepted = new List<AcceptedCandidate>(toScore.Count);
        if (scores is not null)
        {
            for (var i = 0; i < toScore.Count; i++)
            {
                var candidate = toScore[i];
                accepted.Add(new AcceptedCandidate(candidate.LineNumber, candidate.Transaction, candidate.Fingerprint, scores[i]));
            }
        }

        var commit = new BatchCommit(
            job.ImportId,
            leaseToken,
            job.ProviderId,
            accepted,
            rejections,
            batch.EndOffset,
            batch.EndLineNumber,
            _options.LeaseDuration);

        stageStart = timeProvider.GetTimestamp();
        var result = await retries.ExecuteAsync(
                "commit-batch",
                ct => store.CommitBatchAsync(commit, ct),
                control.WorkToken)
            .ConfigureAwait(false);
        metrics.BatchStage("commit", timeProvider.GetElapsedTime(stageStart));

        if (result.State == LeaseState.Active)
        {
            metrics.RecordsCommitted(result.Accepted, result.Rejected, result.Duplicates);
            foreach (var conflict in result.ConflictRejections)
            {
                metrics.RecordRejected(conflict.Code);
            }

            LogBatch(logger, job.ImportId, batch.Number, result.Accepted, result.Rejected, result.Duplicates, batch.EndLineNumber);
        }

        return result;
    }

    private async Task HeartbeatAsync(Guid importId, Guid leaseToken, ProcessingControl control, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                LeaseState state;
                try
                {
                    state = await store.RenewLeaseAsync(importId, leaseToken, _options.LeaseDuration, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogHeartbeatFailed(logger, exception, importId);
                    continue;
                }

                switch (state)
                {
                    case LeaseState.CancelRequested:
                        control.RequestStop(StopReason.CancelRequested);
                        return;
                    case LeaseState.Lost:
                        control.RequestStop(StopReason.LeaseLost);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<ProcessingOutcome> FinalizeAsync(Guid importId, Guid leaseToken, ProcessingControl control)
    {
        using var timeout = new CancellationTokenSource(_options.FinalizeTimeout);
        try
        {
            return control.Reason switch
            {
                StopReason.CancelRequested => await FinishAsync(
                        () => store.ConfirmCancelledAsync(importId, leaseToken, timeout.Token), ProcessingOutcome.Cancelled)
                    .ConfigureAwait(false),
                StopReason.LeaseLost => ProcessingOutcome.LeaseLost,
                _ when control.ShutdownRequested => await FinishAsync(
                        () => store.ReleaseAsync(importId, leaseToken, TimeSpan.Zero, timeout.Token), ProcessingOutcome.Released)
                    .ConfigureAwait(false),
                _ => await FinishAsync(
                        () => store.CompleteAsync(importId, leaseToken, timeout.Token), ProcessingOutcome.Completed)
                    .ConfigureAwait(false),
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogFinalizeFailed(logger, exception, importId);
            return ProcessingOutcome.Abandoned;
        }
    }

    private async Task<ProcessingOutcome> FailOrReleaseAsync(Guid importId, Guid leaseToken, int attempt, Exception exception)
    {
        using var timeout = new CancellationTokenSource(_options.FinalizeTimeout);
        try
        {
            if (exception is AppException { Retryable: false } or StorageObjectNotFoundException)
            {
                var code = exception is AppException app ? app.Code : "STORAGE_OBJECT_MISSING";
                var reason = exception is AppException app2 ? app2.Message : "The uploaded file is missing.";
                return await FinishAsync(() => store.FailAsync(importId, leaseToken, code, reason, timeout.Token), ProcessingOutcome.Failed)
                    .ConfigureAwait(false);
            }

            var delay = DelayForAttempt(attempt);
            return await FinishAsync(() => store.ReleaseAsync(importId, leaseToken, delay, timeout.Token), ProcessingOutcome.Released)
                .ConfigureAwait(false);
        }
        catch (Exception finalizeException) when (finalizeException is not OutOfMemoryException)
        {
            LogFinalizeFailed(logger, finalizeException, importId);
            return ProcessingOutcome.Abandoned;
        }
    }

    private static async Task<ProcessingOutcome> FinishAsync(Func<Task<FinishResult>> action, ProcessingOutcome success)
    {
        var result = await action().ConfigureAwait(false);
        return result switch
        {
            FinishResult.Done => success,
            FinishResult.Cancelled => ProcessingOutcome.Cancelled,
            FinishResult.LeaseLost => ProcessingOutcome.LeaseLost,
            _ => ProcessingOutcome.Abandoned,
        };
    }

    private TimeSpan DelayForAttempt(int attempt)
    {
        var factor = Math.Pow(2, Math.Max(0, attempt - 1));
        var delay = TimeSpan.FromTicks((long)(_options.JobRetryBaseDelay.Ticks * factor));
        return delay > _options.JobRetryMaxDelay ? _options.JobRetryMaxDelay : delay;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Claimed import {ImportId} (attempt {Attempt}, resume at line {LineNumber})")]
    private static partial void LogClaimed(ILogger logger, Guid importId, int attempt, long lineNumber);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Import {ImportId} batch {BatchNumber}: +{Accepted} accepted, +{Rejected} rejected, +{Duplicates} duplicates (through line {LineNumber})")]
    private static partial void LogBatch(ILogger logger, Guid importId, int batchNumber, long accepted, long rejected, long duplicates, long lineNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to claim import {ImportId}")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, Guid importId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing failed for import {ImportId}")]
    private static partial void LogProcessFailed(ILogger logger, Exception exception, Guid importId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease heartbeat failed for import {ImportId}")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception exception, Guid importId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to finalize import {ImportId}")]
    private static partial void LogFinalizeFailed(ILogger logger, Exception exception, Guid importId);
}
