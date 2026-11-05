namespace TransactionImport.Application.Processing;

internal enum StopReason
{
    None,
    CancelRequested,
    LeaseLost,
}

/// <summary>
/// Separates the three reasons processing stops, because each ends differently:
/// shutdown → finish the in-flight batch, then release the job; user cancellation → commit nothing more,
/// confirm <c>cancelled</c>; lease lost → stop immediately, another worker owns the import.
/// </summary>
internal sealed class ProcessingControl : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _read;
    private int _reason;

    public ProcessingControl(CancellationToken shutdownToken)
    {
        ShutdownToken = shutdownToken;
        _read = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, _stop.Token);
    }

    public CancellationToken ShutdownToken { get; }

    /// <summary>Cancelled on user cancellation or lease loss, but not on shutdown (in-flight work may finish).</summary>
    public CancellationToken WorkToken => _stop.Token;

    /// <summary>Cancelled on any stop reason, including shutdown; stops reading new lines.</summary>
    public CancellationToken ReadToken => _read.Token;

    public bool ShutdownRequested => ShutdownToken.IsCancellationRequested;

    public StopReason Reason => (StopReason)Volatile.Read(ref _reason);

    public void RequestStop(StopReason reason)
    {
        Interlocked.CompareExchange(ref _reason, (int)reason, (int)StopReason.None);
        _stop.Cancel();
    }

    public void StopReading() => _read.Cancel();

    public void Dispose()
    {
        _read.Dispose();
        _stop.Dispose();
    }
}
