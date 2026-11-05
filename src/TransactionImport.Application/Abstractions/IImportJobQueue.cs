namespace TransactionImport.Application.Abstractions;

public sealed record ImportJobMessage(Guid ImportId, string? TraceParent);

/// <summary>A delivered queue message. <see cref="ImportId"/> is null when the payload could not be parsed.</summary>
public sealed record ReceivedJob(string MessageId, Guid? ImportId, string? TraceParent, long DeliveryCount);

public sealed record QueueStatistics(long Length, long Pending, long DeadLettered);

/// <summary>
/// At-least-once job notification queue. Messages only say "import X has work"; the database lease decides
/// who actually processes it, so duplicate or stale deliveries are harmless.
/// </summary>
public interface IImportJobQueue
{
    Task PublishAsync(ImportJobMessage message, CancellationToken cancellationToken);

    /// <summary>Returns up to <paramref name="maxCount"/> messages: first ones abandoned by dead consumers, then new ones.</summary>
    Task<IReadOnlyList<ReceivedJob>> ReceiveAsync(int maxCount, CancellationToken cancellationToken);

    Task AcknowledgeAsync(ReceivedJob job, CancellationToken cancellationToken);

    /// <summary>Signals that the message is still being worked on so it is not reclaimed by another consumer.</summary>
    Task KeepAliveAsync(ReceivedJob job, CancellationToken cancellationToken);

    /// <summary>Moves the message to the dead-letter stream and acknowledges it.</summary>
    Task DeadLetterAsync(ReceivedJob job, string reason, CancellationToken cancellationToken);

    Task<QueueStatistics> GetStatisticsAsync(CancellationToken cancellationToken);
}
