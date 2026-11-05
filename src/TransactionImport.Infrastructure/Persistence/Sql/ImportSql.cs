using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TransactionImport.Application.Imports;
using TransactionImport.Application.Processing;
using TransactionImport.Domain.Imports;
using TransactionImport.Domain.Transactions;

namespace TransactionImport.Infrastructure.Persistence.Sql;

internal static class ImportSql
{
    public const string OutboxTopicImportJobs = "imports.jobs";

    public static string ToDb(ImportStatus status) => status switch
    {
        ImportStatus.Pending => "pending",
        ImportStatus.Processing => "processing",
        ImportStatus.Completed => "completed",
        ImportStatus.Failed => "failed",
        ImportStatus.Cancelling => "cancelling",
        ImportStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static ImportStatus FromDb(string status) => status switch
    {
        "pending" => ImportStatus.Pending,
        "processing" => ImportStatus.Processing,
        "completed" => ImportStatus.Completed,
        "failed" => ImportStatus.Failed,
        "cancelling" => ImportStatus.Cancelling,
        "cancelled" => ImportStatus.Cancelled,
        _ => throw new InvalidOperationException($"Unknown import status '{status}'."),
    };

    public static string ToDb(RiskLevel level) => level switch
    {
        RiskLevel.Low => "low",
        RiskLevel.Medium => "medium",
        RiskLevel.High => "high",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static RiskLevel FromDbRisk(string level) => level switch
    {
        "low" => RiskLevel.Low,
        "medium" => RiskLevel.Medium,
        "high" => RiskLevel.High,
        _ => throw new InvalidOperationException($"Unknown risk level '{level}'."),
    };

    public static ImportView ReadImport(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        FromDb(reader.GetString(2)),
        reader.GetString(3),
        reader.GetInt64(4),
        new ImportProgress(reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8)),
        reader.GetInt32(9),
        reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
        reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14));

    public const string ImportSelectColumns = """
        id, provider_id, status, file_name, file_size_bytes,
        processed_count, accepted_count, rejected_count, duplicate_count,
        attempt_count, created_at, started_at, completed_at, failure_code, failure_reason
        """;

    public static async Task EnqueueOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid importId, string? traceParent, DateTimeOffset availableAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO outbox_messages (topic, payload, created_at, available_at)
            VALUES (@topic, @payload, @now, @availableAt)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("topic", OutboxTopicImportJobs);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(new { importId, traceParent }),
        });
        command.Parameters.AddWithValue("now", availableAt);
        command.Parameters.AddWithValue("availableAt", availableAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void ApplyProviderScope(NpgsqlCommand command, ProviderScope scope, string alias = "i")
    {
        if (scope.ProviderId is null)
        {
            command.CommandText = command.CommandText.Replace("/*provider*/", string.Empty, StringComparison.Ordinal);
            return;
        }

        command.CommandText = command.CommandText.Replace(
            "/*provider*/",
            $" AND {alias}.provider_id = @providerId",
            StringComparison.Ordinal);
        command.Parameters.AddWithValue("providerId", scope.ProviderId);
    }
}
