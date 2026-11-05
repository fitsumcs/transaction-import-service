using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TransactionImport.Application.Abstractions;

namespace TransactionImport.Infrastructure.Persistence;

/// <summary>
/// Relays unpublished outbox rows to the Redis job stream. Writing the outbox row in the same database
/// transaction as the state change makes publish atomic; this relay provides at-least-once delivery.
/// </summary>
internal sealed partial class OutboxRelayHostedService(
    NpgsqlDataSource dataSource,
    IImportJobQueue queue,
    ILogger<OutboxRelayHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int published;
            try
            {
                published = await PublishBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogRelayFailed(logger, exception);
                published = 0;
            }

            if (published == 0)
            {
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var select = new NpgsqlCommand(
            """
            SELECT id, payload, attempts
            FROM outbox_messages
            WHERE published_at IS NULL AND available_at <= now()
            ORDER BY id
            FOR UPDATE SKIP LOCKED
            LIMIT 50
            """,
            connection, transaction);

        var rows = new List<(long Id, string Payload, int Attempts)>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }

        var published = 0;
        foreach (var row in rows)
        {
            try
            {
                var message = JsonSerializer.Deserialize<OutboxPayload>(row.Payload, JsonOptions)
                    ?? throw new InvalidOperationException("Outbox payload was null.");
                await queue.PublishAsync(new ImportJobMessage(message.ImportId, message.TraceParent), cancellationToken)
                    .ConfigureAwait(false);

                await using var mark = new NpgsqlCommand(
                    "UPDATE outbox_messages SET published_at = now(), last_error = NULL WHERE id = @id",
                    connection, transaction);
                mark.Parameters.AddWithValue("id", row.Id);
                await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                published++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, row.Attempts)));
                await using var fail = new NpgsqlCommand(
                    """
                    UPDATE outbox_messages
                    SET attempts = attempts + 1, last_error = @error, available_at = now() + @delay
                    WHERE id = @id
                    """,
                    connection, transaction);
                fail.Parameters.AddWithValue("error", exception.Message.Length > 500 ? exception.Message[..500] : exception.Message);
                fail.Parameters.AddWithValue("delay", delay);
                fail.Parameters.AddWithValue("id", row.Id);
                await fail.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                LogPublishFailed(logger, exception, row.Id);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return published;
    }

    private sealed record OutboxPayload(Guid ImportId, string? TraceParent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox relay loop failed")]
    private static partial void LogRelayFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to publish outbox message {OutboxId}")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, long outboxId);
}
