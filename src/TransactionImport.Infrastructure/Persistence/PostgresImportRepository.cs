using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TransactionImport.Application.Imports;
using TransactionImport.Application.Processing;
using TransactionImport.Domain.Imports;
using TransactionImport.Domain.Transactions;
using TransactionImport.Infrastructure.Persistence.Sql;

namespace TransactionImport.Infrastructure.Persistence;

internal sealed class PostgresImportRepository(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IImportRepository
{
    public async Task<IdempotencyReservation> ReserveIdempotencyKeyAsync(
        string providerId, string key, Guid lockToken, TimeSpan lockDuration, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO idempotency_keys (provider_id, idempotency_key, status, lock_token, locked_until, created_at)
            VALUES (@providerId, @key, 'in_progress', @lockToken, @lockedUntil, @now)
            ON CONFLICT (provider_id, idempotency_key) DO NOTHING
            """,
            connection, transaction))
        {
            insert.Parameters.AddWithValue("providerId", providerId);
            insert.Parameters.AddWithValue("key", key);
            insert.Parameters.AddWithValue("lockToken", lockToken);
            insert.Parameters.AddWithValue("lockedUntil", now + lockDuration);
            insert.Parameters.AddWithValue("now", now);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new IdempotencyReservation(IdempotencyReservationKind.Reserved, null);
            }
        }

        await using var select = new NpgsqlCommand(
            """
            SELECT status, import_id, locked_until
            FROM idempotency_keys
            WHERE provider_id = @providerId AND idempotency_key = @key
            FOR UPDATE
            """,
            connection, transaction);
        select.Parameters.AddWithValue("providerId", providerId);
        select.Parameters.AddWithValue("key", key);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Idempotency key vanished after conflict.");
        }

        var status = reader.GetString(0);
        var importId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        var lockedUntil = reader.GetFieldValue<DateTimeOffset>(2);
        await reader.CloseAsync().ConfigureAwait(false);

        if (status == "completed")
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new IdempotencyReservation(IdempotencyReservationKind.Replay, importId);
        }

        if (lockedUntil < now)
        {
            await using var takeOver = new NpgsqlCommand(
                """
                UPDATE idempotency_keys
                SET lock_token = @lockToken, locked_until = @lockedUntil
                WHERE provider_id = @providerId AND idempotency_key = @key AND status = 'in_progress'
                """,
                connection, transaction);
            takeOver.Parameters.AddWithValue("lockToken", lockToken);
            takeOver.Parameters.AddWithValue("lockedUntil", now + lockDuration);
            takeOver.Parameters.AddWithValue("providerId", providerId);
            takeOver.Parameters.AddWithValue("key", key);
            await takeOver.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new IdempotencyReservation(IdempotencyReservationKind.Reserved, null);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new IdempotencyReservation(IdempotencyReservationKind.InProgress, null);
    }

    public async Task ReleaseIdempotencyKeyAsync(string providerId, string key, Guid lockToken, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            DELETE FROM idempotency_keys
            WHERE provider_id = @providerId AND idempotency_key = @key
              AND lock_token = @lockToken AND status = 'in_progress'
            """);
        command.Parameters.AddWithValue("providerId", providerId);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("lockToken", lockToken);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportView?> CreateAsync(NewImport import, string idempotencyKey, Guid lockToken, string? traceParent, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO imports (
                id, provider_id, status, file_name, storage_key, file_size_bytes, file_sha256,
                max_attempts, last_enqueued_at, created_at, updated_at)
            VALUES (
                @id, @providerId, 'pending', @fileName, @storageKey, @fileSize, @sha256,
                @maxAttempts, @now, @now, @now)
            """,
            connection, transaction))
        {
            insert.Parameters.AddWithValue("id", import.Id);
            insert.Parameters.AddWithValue("providerId", import.ProviderId);
            insert.Parameters.AddWithValue("fileName", import.FileName);
            insert.Parameters.AddWithValue("storageKey", import.StorageKey);
            insert.Parameters.AddWithValue("fileSize", import.FileSizeBytes);
            insert.Parameters.AddWithValue("sha256", import.FileSha256);
            insert.Parameters.AddWithValue("maxAttempts", import.MaxAttempts);
            insert.Parameters.AddWithValue("now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var complete = new NpgsqlCommand(
            """
            UPDATE idempotency_keys
            SET status = 'completed', import_id = @importId, completed_at = @now
            WHERE provider_id = @providerId AND idempotency_key = @key
              AND lock_token = @lockToken AND status = 'in_progress'
            """,
            connection, transaction))
        {
            complete.Parameters.AddWithValue("importId", import.Id);
            complete.Parameters.AddWithValue("now", now);
            complete.Parameters.AddWithValue("providerId", import.ProviderId);
            complete.Parameters.AddWithValue("key", idempotencyKey);
            complete.Parameters.AddWithValue("lockToken", lockToken);
            if (await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        await ImportSql.EnqueueOutboxAsync(connection, transaction, import.Id, traceParent, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ImportView(
            import.Id, import.ProviderId, ImportStatus.Pending, import.FileName, import.FileSizeBytes,
            ImportProgress.Empty, 0, now, null, null, null, null);
    }

    public async Task<ImportView?> GetAsync(Guid importId, ProviderScope scope, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            SELECT {ImportSql.ImportSelectColumns}
            FROM imports i
            WHERE i.id = @id /*provider*/
            """);
        command.Parameters.AddWithValue("id", importId);
        ImportSql.ApplyProviderScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ImportSql.ReadImport(reader) : null;
    }

    public async Task<CancelResult> RequestCancellationAsync(Guid importId, ProviderScope scope, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var select = new NpgsqlCommand(
            "SELECT status FROM imports i WHERE i.id = @id /*provider*/ FOR UPDATE",
            connection, transaction);
        select.Parameters.AddWithValue("id", importId);
        ImportSql.ApplyProviderScope(select, scope);
        var statusObj = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (statusObj is null)
        {
            return new CancelResult(CancelOutcome.NotFound, null);
        }

        var status = ImportSql.FromDb((string)statusObj);
        switch (status)
        {
            case ImportStatus.Cancelled:
                return new CancelResult(CancelOutcome.Cancelled, status);
            case ImportStatus.Cancelling:
                return new CancelResult(CancelOutcome.AlreadyCancelling, status);
            case ImportStatus.Completed:
            case ImportStatus.Failed:
                return new CancelResult(CancelOutcome.NotCancellable, status);
            case ImportStatus.Pending:
                await UpdateStatusAsync(connection, transaction, importId, ImportStatus.Cancelled, now, setCompleted: true, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new CancelResult(CancelOutcome.Cancelled, ImportStatus.Cancelled);
            default:
                await UpdateStatusAsync(connection, transaction, importId, ImportStatus.Cancelling, now, setCompleted: false, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new CancelResult(CancelOutcome.CancellationRequested, ImportStatus.Cancelling);
        }
    }

    public async Task<RetryResult> RetryAsync(Guid importId, ProviderScope scope, string? traceParent, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var select = new NpgsqlCommand(
            "SELECT status FROM imports i WHERE i.id = @id /*provider*/ FOR UPDATE",
            connection, transaction);
        select.Parameters.AddWithValue("id", importId);
        ImportSql.ApplyProviderScope(select, scope);
        var statusObj = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (statusObj is null)
        {
            return new RetryResult(RetryOutcome.NotFound, null);
        }

        var status = ImportSql.FromDb((string)statusObj);
        if (status != ImportStatus.Failed)
        {
            return new RetryResult(RetryOutcome.NotRetryable, status);
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE imports
            SET status = 'pending', attempt_count = 0, failure_code = NULL, failure_reason = NULL,
                completed_at = NULL, last_enqueued_at = @now, updated_at = @now,
                locked_by = NULL, lease_token = NULL, lease_expires_at = NULL
            WHERE id = @id
            """,
            connection, transaction))
        {
            update.Parameters.AddWithValue("id", importId);
            update.Parameters.AddWithValue("now", now);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ImportSql.EnqueueOutboxAsync(connection, transaction, importId, traceParent, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RetryResult(RetryOutcome.Requeued, ImportStatus.Pending);
    }

    public async Task<ImportSummary?> GetSummaryAsync(Guid importId, ProviderScope scope, int topGroups, CancellationToken cancellationToken)
    {
        var import = await GetAsync(importId, scope, cancellationToken).ConfigureAwait(false);
        if (import is null)
        {
            return null;
        }

        await using var snapshot = dataSource.CreateCommand("SELECT summary FROM import_summaries WHERE import_id = @id");
        snapshot.Parameters.AddWithValue("id", importId);
        var json = await snapshot.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is string text)
        {
            var stored = JsonSerializer.Deserialize<ImportSummary>(text)
                ?? throw new InvalidOperationException($"Corrupt summary snapshot for import {importId}.");
            return stored with { IsFinal = true };
        }

        return await ComputeSummaryAsync(import, topGroups, isFinal: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecordRejection>?> GetRejectionsAsync(
        Guid importId, ProviderScope scope, long afterLine, int limit, CancellationToken cancellationToken)
    {
        if (await GetAsync(importId, scope, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        await using var command = dataSource.CreateCommand(
            """
            SELECT line_number, reason, message, transaction_id, raw_value
            FROM rejected_records
            WHERE import_id = @id AND line_number > @after
            ORDER BY line_number
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("id", importId);
        command.Parameters.AddWithValue("after", afterLine);
        command.Parameters.AddWithValue("limit", limit + 1);
        var items = new List<RecordRejection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new RecordRejection(
                reader.GetInt64(0),
                RejectionCodeExtensions.ParseApi(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return items;
    }

    internal async Task<ImportSummary> ComputeSummaryAsync(ImportView import, int topGroups, bool isFinal, CancellationToken cancellationToken)
    {
        await using var byCurrency = dataSource.CreateCommand(
            """
            SELECT currency, COUNT(*), SUM(amount)
            FROM transactions WHERE import_id = @id
            GROUP BY currency ORDER BY currency
            """);
        byCurrency.Parameters.AddWithValue("id", import.Id);
        var currencies = new List<CurrencyTotal>();
        await using (var reader = await byCurrency.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                currencies.Add(new CurrencyTotal(reader.GetString(0), reader.GetInt64(1), reader.GetDecimal(2)));
            }
        }

        await using var byRisk = dataSource.CreateCommand(
            """
            SELECT risk_level, COUNT(*) FROM transactions WHERE import_id = @id GROUP BY risk_level
            """);
        byRisk.Parameters.AddWithValue("id", import.Id);
        long low = 0, medium = 0, high = 0;
        await using (var reader = await byRisk.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (ImportSql.FromDbRisk(reader.GetString(0)))
                {
                    case Domain.Transactions.RiskLevel.Low: low = reader.GetInt64(1); break;
                    case Domain.Transactions.RiskLevel.Medium: medium = reader.GetInt64(1); break;
                    case Domain.Transactions.RiskLevel.High: high = reader.GetInt64(1); break;
                }
            }
        }

        async Task<IReadOnlyList<GroupTotal>> TopAsync(string column)
        {
            await using var command = dataSource.CreateCommand(
                $"""
                SELECT {column}, currency, COUNT(*), SUM(amount)
                FROM transactions WHERE import_id = @id
                GROUP BY {column}, currency
                ORDER BY SUM(amount) DESC
                LIMIT @limit
                """);
            command.Parameters.AddWithValue("id", import.Id);
            command.Parameters.AddWithValue("limit", topGroups);
            var items = new List<GroupTotal>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new GroupTotal(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetDecimal(3)));
            }

            return items;
        }

        return new ImportSummary(
            import.Id,
            import.Progress.Accepted,
            import.Progress.Rejected,
            import.Progress.Duplicates,
            currencies,
            new RiskLevelCounts(low, medium, high),
            await TopAsync("merchant_id").ConfigureAwait(false),
            await TopAsync("account_id").ConfigureAwait(false),
            isFinal);
    }

    private static async Task UpdateStatusAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid importId, ImportStatus status,
        DateTimeOffset now, bool setCompleted, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            setCompleted
                ? "UPDATE imports SET status = @status, completed_at = @now, updated_at = @now, locked_by = NULL, lease_token = NULL, lease_expires_at = NULL WHERE id = @id"
                : "UPDATE imports SET status = @status, updated_at = @now WHERE id = @id",
            connection, transaction);
        command.Parameters.AddWithValue("status", ImportSql.ToDb(status));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("id", importId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
