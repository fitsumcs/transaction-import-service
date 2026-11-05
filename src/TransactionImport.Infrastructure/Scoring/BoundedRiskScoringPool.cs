using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TransactionImport.Application.Abstractions;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.Infrastructure.Scoring;

/// <summary>
/// Reusable worker-thread pool for CPU-bound risk scoring. Requests flow through a bounded channel
/// (backpressure when full). One pool is shared by all imports in the process — never one thread per
/// transaction or per request. Dedicated threads (not the thread-pool) keep scoring off the request
/// and I/O paths.
/// </summary>
internal sealed class BoundedRiskScoringPool : IRiskScoringPool, IHostedService, IDisposable
{
    private readonly IRiskScorer _scorer;
    private readonly RiskScoringOptions _options;
    private readonly Channel<WorkItem> _channel;
    private readonly Thread[] _workers;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _queued;
    private int _active;
    private volatile bool _stopping;

    public BoundedRiskScoringPool(IRiskScorer scorer, IOptions<RiskScoringOptions> options)
    {
        _scorer = scorer;
        _options = options.Value;
        var workers = _options.WorkerCount > 0 ? _options.WorkerCount : Math.Max(1, Environment.ProcessorCount);
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_options.MaxQueuedRequests)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        _workers = new Thread[workers];
        for (var i = 0; i < workers; i++)
        {
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"risk-scorer-{i}",
                Priority = ThreadPriority.BelowNormal,
            };
            _workers[i] = thread;
        }
    }

    public int Queued => Volatile.Read(ref _queued);

    public int Active => Volatile.Read(ref _active);

    public int WorkerCount => _workers.Length;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var worker in _workers)
        {
            worker.Start();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _channel.Writer.TryComplete();
        await _stopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RiskScore[]> ScoreAsync(IReadOnlyList<ScoringRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        if (_stopping)
        {
            throw new OperationCanceledException("Risk scoring pool is stopping.");
        }

        var scores = new RiskScore[requests.Count];
        var chunkSize = Math.Max(1, _options.ChunkSize);
        var chunks = (requests.Count + chunkSize - 1) / chunkSize;
        var remaining = chunks;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        for (var offset = 0; offset < requests.Count; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, requests.Count - offset);
            Interlocked.Increment(ref _queued);
            try
            {
                await _channel.Writer.WriteAsync(new WorkItem(requests, scores, offset, length, tcs, () =>
                {
                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        tcs.TrySetResult();
                    }
                }), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Decrement(ref _queued);
                throw;
            }
        }

        await tcs.Task.ConfigureAwait(false);
        return scores;
    }

    public void Dispose() => _channel.Writer.TryComplete();

    private void WorkerLoop()
    {
        try
        {
            var reader = _channel.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var item))
                {
                    Interlocked.Decrement(ref _queued);
                    Interlocked.Increment(ref _active);
                    try
                    {
                        for (var i = 0; i < item.Length; i++)
                        {
                            var request = item.Requests[item.Offset + i];
                            item.Scores[item.Offset + i] = _scorer.Score(request.Transaction, request.Fingerprint);
                        }

                        item.OnCompleted();
                    }
                    catch (Exception exception)
                    {
                        item.Completion.TrySetException(exception);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _active);
                    }
                }
            }
        }
        finally
        {
            if (_workers.Count(thread => thread.IsAlive || thread == Thread.CurrentThread) <= 1)
            {
                _stopped.TrySetResult();
            }
        }
    }

    private sealed record WorkItem(
        IReadOnlyList<ScoringRequest> Requests,
        RiskScore[] Scores,
        int Offset,
        int Length,
        TaskCompletionSource Completion,
        Action OnCompleted);
}
