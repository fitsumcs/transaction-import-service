using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TransactionImport.Application.Abstractions;
using TransactionImport.Application.Processing;

namespace TransactionImport.Infrastructure.Queue;

/// <summary>
/// Redis Streams job queue with a consumer group and a dead-letter stream.
/// <list type="bullet">
/// <item>XADD publishes; XREADGROUP consumes; XACK acknowledges.</item>
/// <item>XAUTOCLAIM reclaims messages abandoned by dead consumers (lease-style).</item>
/// <item>After <see cref="RedisOptions.MaxDeliveryAttempts"/> deliveries, the message is XADD'd to the DLQ and ACKed.</item>
/// </list>
/// The queue is a notification only: the database lease decides who processes an import, so duplicate
/// deliveries are expected and harmless. The transactional outbox (not this type) ensures publish atomicity.
/// </summary>
internal sealed partial class RedisImportJobQueue : IImportJobQueue
{
    private static readonly RedisValue FieldImportId = "importId";
    private static readonly RedisValue FieldTraceParent = "traceparent";
    private static readonly RedisValue FieldReason = "reason";
    private static readonly RedisValue FieldOriginalId = "originalId";
    private static readonly RedisValue FieldPayload = "payload";

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisOptions _options;
    private readonly string _consumerName;
    private readonly ILogger<RedisImportJobQueue> _logger;
    private int _groupReady;

    public RedisImportJobQueue(IConnectionMultiplexer redis, IOptions<RedisOptions> options, IWorkerIdentity worker, ILogger<RedisImportJobQueue> logger)
    {
        _redis = redis;
        _options = options.Value;
        _consumerName = worker.WorkerId;
        _logger = logger;
    }

    public async Task PublishAsync(ImportJobMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var db = _redis.GetDatabase();
        await db.StreamAddAsync(
            _options.StreamKey,
            [
                new NameValueEntry(FieldImportId, message.ImportId.ToString("N")),
                new NameValueEntry(FieldTraceParent, message.TraceParent ?? string.Empty),
            ]).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReceivedJob>> ReceiveAsync(int maxCount, CancellationToken cancellationToken)
    {
        await EnsureGroupAsync().ConfigureAwait(false);
        var db = _redis.GetDatabase();
        var jobs = new List<ReceivedJob>(maxCount);

        try
        {
            var claimed = await db.StreamAutoClaimAsync(
                _options.StreamKey,
                _options.ConsumerGroup,
                _consumerName,
                (long)_options.ClaimIdleTime.TotalMilliseconds,
                "0-0",
                maxCount).ConfigureAwait(false);

            foreach (var entry in claimed.ClaimedEntries)
            {
                jobs.Add(ToJob(entry));
                if (jobs.Count == maxCount)
                {
                    return jobs;
                }
            }

            var remaining = maxCount - jobs.Count;
            if (remaining > 0)
            {
                var read = await db.StreamReadGroupAsync(
                    _options.StreamKey,
                    _options.ConsumerGroup,
                    _consumerName,
                    ">",
                    remaining).ConfigureAwait(false);
                jobs.AddRange(read.Select(ToJob));
            }
        }
        catch (RedisException exception) when (IsTransient(exception))
        {
            throw;
        }

        return jobs;
    }

    public Task AcknowledgeAsync(ReceivedJob job, CancellationToken cancellationToken) =>
        _redis.GetDatabase().StreamAcknowledgeAsync(_options.StreamKey, _options.ConsumerGroup, job.MessageId);

    public Task KeepAliveAsync(ReceivedJob job, CancellationToken cancellationToken) =>
        // Redis Streams has no per-message soft claim refresh; idle time is measured from last delivery.
        // The database lease is the authoritative ownership signal; XAUTOCLAIM idle is set longer than the lease.
        Task.CompletedTask;

    public async Task DeadLetterAsync(ReceivedJob job, string reason, CancellationToken cancellationToken)
    {
        var db = _redis.GetDatabase();
        await db.StreamAddAsync(
            _options.DeadLetterStreamKey,
            [
                new NameValueEntry(FieldOriginalId, job.MessageId),
                new NameValueEntry(FieldImportId, job.ImportId?.ToString("N") ?? string.Empty),
                new NameValueEntry(FieldReason, reason),
                new NameValueEntry(FieldPayload, JsonSerializer.Serialize(job)),
                new NameValueEntry(FieldTraceParent, job.TraceParent ?? string.Empty),
            ]).ConfigureAwait(false);
        await AcknowledgeAsync(job, cancellationToken).ConfigureAwait(false);
        LogDeadLetter(_logger, job.MessageId, job.ImportId, reason);
    }

    public async Task<QueueStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        var db = _redis.GetDatabase();
        var length = await db.StreamLengthAsync(_options.StreamKey).ConfigureAwait(false);
        var dlq = await db.StreamLengthAsync(_options.DeadLetterStreamKey).ConfigureAwait(false);
        long pending = 0;
        try
        {
            var info = await db.StreamPendingAsync(_options.StreamKey, _options.ConsumerGroup).ConfigureAwait(false);
            pending = info.PendingMessageCount;
        }
        catch (RedisServerException)
        {
            // Group may not exist yet.
        }

        return new QueueStatistics(length, pending, dlq);
    }

    private ReceivedJob ToJob(StreamEntry entry)
    {
        Guid? importId = null;
        if (entry[FieldImportId] is { HasValue: true } value
            && Guid.TryParseExact((string)value!, "N", out var parsed))
        {
            importId = parsed;
        }

        var trace = entry[FieldTraceParent];
        // Approximate delivery count via pending info is expensive per message; the processor uses the
        // database attempt counter for retry budgets. DeliveryCount here is 1 for new reads.
        return new ReceivedJob(entry.Id!, importId, trace.HasValue && !trace.IsNullOrEmpty ? (string)trace! : null, 1);
    }

    private async Task EnsureGroupAsync()
    {
        if (Interlocked.CompareExchange(ref _groupReady, 1, 0) == 1)
        {
            return;
        }

        try
        {
            await _redis.GetDatabase().StreamCreateConsumerGroupAsync(_options.StreamKey, _options.ConsumerGroup, "0-0", createStream: true)
                .ConfigureAwait(false);
        }
        catch (RedisServerException exception) when (exception.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
        }
        catch
        {
            Interlocked.Exchange(ref _groupReady, 0);
            throw;
        }
    }

    internal static bool IsTransient(Exception exception) =>
        exception is RedisTimeoutException or RedisConnectionException
            || exception is SocketException
            || (exception is RedisException && exception.InnerException is SocketException);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead-lettered queue message {MessageId} for import {ImportId}: {Reason}")]
    private static partial void LogDeadLetter(ILogger logger, string messageId, Guid? importId, string reason);
}
