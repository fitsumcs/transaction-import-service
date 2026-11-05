using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TransactionImport.Application.Imports;
using TransactionImport.Application.Processing;
using TransactionImport.Domain.Imports;
using TransactionImport.Domain.Transactions;
using TransactionImport.Infrastructure.Persistence.Sql;

namespace TransactionImport.Infrastructure.Persistence;

internal sealed class PostgresImportProcessingStore(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IImportProcessingStore
{
    public async Task<ClaimResult> TryClaimAsync(Guid importId, string workerId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var select = new NpgsqlCommand(
            """
            SELECT status, provider_id, storage_key, checkpoint_byte_offset, checkpoint_line_number,
                   attempt_count, max_attempts, processed_count, accepted_count, rejected_count, duplicate_count,
                   lease_token, lease_expires_at
            FROM imports WHERE id = @id FOR UPDATE SKIP LOCKED
            """,
            connection, transaction);
        select.Parameters.AddWithValue("id", importId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Either missing or locked by another transaction.
            await reader.CloseAsync().ConfigureAwait(false);
            await using var exists = new NpgsqlCommand("SELECT 1 FROM imports WHERE id = @id", connection, transaction);
            exists.Parameters.AddWithValue("id", importId);
            return await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null
                ? new ClaimResult(ClaimStatus.NotFound, null)
                : new ClaimResult(ClaimStatus.NotClaimable, null);
        }

        var status = ImportSql.FromDb(reader.GetString(0));
        var providerId = reader.GetString(1);
        var storageKey = reader.GetString(2);
        var checkpointOffset = reader.GetInt64(3);
        var checkpointLine = reader.GetInt64(4);
        var attemptCount = reader.GetInt32(5);
        var maxAttempts = reader.GetInt32(6);
        var progress = new ImportProgress(reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10));
        var existingLease = reader.IsDBNull(11) ? (Guid?)null : reader.GetGuid(11);
        var leaseExpires = reader.IsDBNull(12) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(12);
        await reader.CloseAsync().ConfigureAwait(false);

        var leaseActive = existingLease is not null && leaseExpires > now;

        if (status == ImportStatus.Cancelling && !leaseActive)
        {
            await FinalizeStatusAsync(connection, transaction, importId, ImportStatus.Cancelled, null, null, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ClaimResult(ClaimStatus.CancelConfirmed, null);
        }

        if (status is not (ImportStatus.Pending or ImportStatus.Processing) || leaseActive)
        {
            return new ClaimResult(ClaimStatus.NotClaimable, null);
        }

        if (attemptCount >= maxAttempts)
        {
            await FinalizeStatusAsync(connection, transaction, importId, ImportStatus.Failed, "MAX_ATTEMPTS", "Processing attempts exhausted.", now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ClaimResult(ClaimStatus.AttemptsExhausted, null);
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE imports
            SET status = 'processing', locked_by = @workerId, lease_token = @leaseToken,
                lease_expires_at = @leaseExpires, attempt_count = attempt_count + 1,
                started_at = COALESCE(started_at, @now), updated_at = @now
            WHERE id = @id
            """,
            connection, transaction))
        {
            update.Parameters.AddWithValue("workerId", workerId);
            update.Parameters.AddWithValue("leaseToken", leaseToken);
            update.Parameters.AddWithValue("leaseExpires", now + leaseDuration);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("id", importId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ClaimResult(
            ClaimStatus.Claimed,
            new ImportJob(importId, providerId, storageKey, checkpointOffset, checkpointLine, attemptCount + 1, maxAttempts, progress));
    }

    public async Task<LeaseState> RenewLeaseAsync(Guid importId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var command = dataSource.CreateCommand(
            """
            UPDATE imports
            SET lease_expires_at = @leaseExpires, updated_at = @now
            WHERE id = @id AND lease_token = @leaseToken
            RETURNING status
            """);
        command.Parameters.AddWithValue("leaseExpires", now + leaseDuration);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("id", importId);
        command.Parameters.AddWithValue("leaseToken", leaseToken);
        var status = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return LeaseState.Lost;
        }

        return ImportSql.FromDb((string)status) == ImportStatus.Cancelling ? LeaseState.CancelRequested : LeaseState.Active;
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> GetExistingFingerprintsAsync(
        string providerId, IReadOnlyCollection<string> transactionIds, CancellationToken cancellationToken)
    {
        if (transactionIds.Count == 0)
        {
            return new Dictionary<string, byte[]>();
        }

        await using var command = dataSource.CreateCommand(
            """
            SELECT transaction_id, fingerprint
            FROM transactions
            WHERE provider_id = @providerId AND transaction_id = ANY(@ids)
            """);
        command.Parameters.AddWithValue("providerId", providerId);
        command.Parameters.AddWithValue("ids", transactionIds.ToArray());
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = (byte[])reader[1];
        }

        return result;
    }

    public async Task<BatchCommitResult> CommitBatchAsync(BatchCommit batch, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var renew = new NpgsqlCommand(
            """
            UPDATE imports
            SET lease_expires_at = @leaseExpires, updated_at = @now
            WHERE id = @id AND lease_token = @leaseToken
            RETURNING status
            """,
            connection, transaction))
        {
            renew.Parameters.AddWithValue("leaseExpires", now + batch.LeaseExtension);
            renew.Parameters.AddWithValue("now", now);
            renew.Parameters.AddWithValue("id", batch.ImportId);
            renew.Parameters.AddWithValue("leaseToken", batch.LeaseToken);
            var statusObj = await renew.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (statusObj is null)
            {
                return BatchCommitResult.Stopped(LeaseState.Lost);
            }

            if (ImportSql.FromDb((string)statusObj) == ImportStatus.Cancelling)
            {
                return BatchCommitResult.Stopped(LeaseState.CancelRequested);
            }
        }

        var acceptedIds = new List<string>();
        if (batch.Candidates.Count > 0)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO transactions (
                    provider_id, transaction_id, import_id, line_number, account_id, merchant_id,
                    amount, currency, occurred_at, description, fingerprint, fingerprint_version,
                    risk_score, risk_level, created_at)
                SELECT @providerId, x.transaction_id, @importId, x.line_number, x.account_id, x.merchant_id,
                       x.amount, x.currency, x.occurred_at, x.description, x.fingerprint, x.fingerprint_version,
                       x.risk_score, x.risk_level, @now
                FROM UNNEST(
                    @transactionIds, @lineNumbers, @accountIds, @merchantIds, @amounts, @currencies,
                    @occurredAts, @descriptions, @fingerprints, @fingerprintVersions, @riskScores, @riskLevels)
                    AS x(transaction_id, line_number, account_id, merchant_id, amount, currency,
                         occurred_at, description, fingerprint, fingerprint_version, risk_score, risk_level)
                ON CONFLICT (provider_id, transaction_id) DO NOTHING
                RETURNING transaction_id
                """,
                connection, transaction);

            insert.Parameters.AddWithValue("providerId", batch.ProviderId);
            insert.Parameters.AddWithValue("importId", batch.ImportId);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.Add(new NpgsqlParameter("transactionIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = batch.Candidates.Select(c => c.Transaction.TransactionId).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("lineNumbers", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = batch.Candidates.Select(c => c.LineNumber).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("accountIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = batch.Candidates.Select(c => c.Transaction.AccountId).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("merchantIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = batch.Candidates.Select(c => c.Transaction.MerchantId).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("amounts", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = batch.Candidates.Select(c => c.Transaction.Amount).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("currencies", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = batch.Candidates.Select(c => c.Transaction.Currency).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("occurredAts", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = batch.Candidates.Select(c => new DateTimeOffset(c.Transaction.OccurredAtUtc)).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("descriptions", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = batch.Candidates.Select(c => c.Transaction.Description).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("fingerprints", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = batch.Candidates.Select(c => c.Fingerprint).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("fingerprintVersions", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = batch.Candidates.Select(_ => TransactionFingerprint.CurrentVersion).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("riskScores", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = batch.Candidates.Select(c => (short)c.Risk.Value).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("riskLevels", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = batch.Candidates.Select(c => ImportSql.ToDb(c.Risk.Level)).ToArray() });

            await using var inserted = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await inserted.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                acceptedIds.Add(inserted.GetString(0));
            }
        }

        var acceptedSet = acceptedIds.ToHashSet(StringComparer.Ordinal);
        var conflictRejections = new List<RecordRejection>();
        var conflicts = batch.Candidates.Where(c => !acceptedSet.Contains(c.Transaction.TransactionId)).ToArray();
        if (conflicts.Length > 0)
        {
            var existing = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            await using (var lookup = new NpgsqlCommand(
                """
                SELECT transaction_id, fingerprint
                FROM transactions
                WHERE provider_id = @providerId AND transaction_id = ANY(@ids)
                """,
                connection, transaction))
            {
                lookup.Parameters.AddWithValue("providerId", batch.ProviderId);
                lookup.Parameters.AddWithValue("ids", conflicts.Select(c => c.Transaction.TransactionId).ToArray());
                await using var existingReader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await existingReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existing[existingReader.GetString(0)] = (byte[])existingReader[1];
                }
            }

            foreach (var conflict in conflicts)
            {
                if (existing.TryGetValue(conflict.Transaction.TransactionId, out var fingerprint))
                {
                    conflictRejections.Add(DuplicateClassifier.Classify(conflict.LineNumber, conflict.Transaction.TransactionId, conflict.Fingerprint, fingerprint));
                }
            }
        }

        var allRejections = batch.Rejections.Concat(conflictRejections).ToArray();
        if (allRejections.Length > 0)
        {
            await using var insertRejections = new NpgsqlCommand(
                """
                INSERT INTO rejected_records (import_id, line_number, reason, message, transaction_id, raw_value, created_at)
                SELECT @importId, x.line_number, x.reason, x.message, x.transaction_id, x.raw_value::jsonb, @now
                FROM UNNEST(@lineNumbers, @reasons, @messages, @transactionIds, @rawValues)
                    AS x(line_number, reason, message, transaction_id, raw_value)
                ON CONFLICT (import_id, line_number) DO NOTHING
                """,
                connection, transaction);
            insertRejections.Parameters.AddWithValue("importId", batch.ImportId);
            insertRejections.Parameters.AddWithValue("now", now);
            insertRejections.Parameters.Add(new NpgsqlParameter("lineNumbers", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = allRejections.Select(r => r.LineNumber).ToArray() });
            insertRejections.Parameters.Add(new NpgsqlParameter("reasons", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = allRejections.Select(r => r.Code.ToApi()).ToArray() });
            insertRejections.Parameters.Add(new NpgsqlParameter("messages", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = allRejections.Select(r => r.Message).ToArray() });
            insertRejections.Parameters.Add(new NpgsqlParameter("transactionIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = allRejections.Select(r => r.TransactionId).ToArray() });
            insertRejections.Parameters.Add(new NpgsqlParameter("rawValues", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = allRejections.Select(r => r.RawValue).ToArray() });
            await insertRejections.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var accepted = acceptedIds.Count;
        var rejected = allRejections.Length;
        var duplicates = allRejections.Count(r => r.Code.IsDuplicate());

        await using (var progress = new NpgsqlCommand(
            """
            UPDATE imports
            SET processed_count = processed_count + @processed,
                accepted_count = accepted_count + @accepted,
                rejected_count = rejected_count + @rejected,
                duplicate_count = duplicate_count + @duplicates,
                checkpoint_byte_offset = @offset,
                checkpoint_line_number = @line,
                updated_at = @now
            WHERE id = @id AND lease_token = @leaseToken
            """,
            connection, transaction))
        {
            progress.Parameters.AddWithValue("processed", accepted + rejected);
            progress.Parameters.AddWithValue("accepted", accepted);
            progress.Parameters.AddWithValue("rejected", rejected);
            progress.Parameters.AddWithValue("duplicates", duplicates);
            progress.Parameters.AddWithValue("offset", batch.CheckpointByteOffset);
            progress.Parameters.AddWithValue("line", batch.CheckpointLineNumber);
            progress.Parameters.AddWithValue("now", now);
            progress.Parameters.AddWithValue("id", batch.ImportId);
            progress.Parameters.AddWithValue("leaseToken", batch.LeaseToken);
            if (await progress.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return BatchCommitResult.Stopped(LeaseState.Lost);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BatchCommitResult(LeaseState.Active, accepted, rejected, duplicates, conflictRejections);
    }

    public async Task<FinishResult> CompleteAsync(Guid importId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var state = await GuardLeaseAsync(connection, transaction, importId, leaseToken, cancellationToken).ConfigureAwait(false);
        if (state is FinishResult.LeaseLost or FinishResult.Cancelled)
        {
            if (state == FinishResult.Cancelled)
            {
                await FinalizeStatusAsync(connection, transaction, importId, ImportStatus.Cancelled, null, null, now, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return state;
        }

        var repo = new PostgresImportRepository(dataSource, timeProvider);
        await using var select = new NpgsqlCommand(
            $"SELECT {ImportSql.ImportSelectColumns} FROM imports i WHERE i.id = @id",
            connection, transaction);
        select.Parameters.AddWithValue("id", importId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var import = ImportSql.ReadImport(reader);
        await reader.CloseAsync().ConfigureAwait(false);

        var summary = await repo.ComputeSummaryAsync(import, topGroups: 20, isFinal: true, cancellationToken).ConfigureAwait(false);
        await using (var insertSummary = new NpgsqlCommand(
            """
            INSERT INTO import_summaries (import_id, summary, computed_at)
            VALUES (@id, @summary, @now)
            ON CONFLICT (import_id) DO UPDATE SET summary = EXCLUDED.summary, computed_at = EXCLUDED.computed_at
            """,
            connection, transaction))
        {
            insertSummary.Parameters.AddWithValue("id", importId);
            insertSummary.Parameters.Add(new NpgsqlParameter("summary", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(summary),
            });
            insertSummary.Parameters.AddWithValue("now", now);
            await insertSummary.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await FinalizeStatusAsync(connection, transaction, importId, ImportStatus.Completed, null, null, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return FinishResult.Done;
    }

    public async Task<FinishResult> ConfirmCancelledAsync(Guid importId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await GuardLeaseAsync(connection, transaction, importId, leaseToken, cancellationToken).ConfigureAwait(false);
        if (state == FinishResult.LeaseLost)
        {
            return state;
        }

        await FinalizeStatusAsync(connection, transaction, importId, ImportStatus.Cancelled, null, null, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return FinishResult.Done;
    }

    public async Task<FinishResult> ReleaseAsync(Guid importId, Guid leaseToken, TimeSpan delay, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await GuardLeaseAsync(connection, transaction, importId, leaseToken, cancellationToken).ConfigureAwait(false);
        if (state is FinishResult.LeaseLost)
        {
            return state;
        }

        if (state == FinishResult.Cancelled)
        {
            await FinalizeStatusAsync(connection, transaction, importId, ImportStatus.Cancelled, null, null, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return FinishResult.Cancelled;
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE imports
            SET status = 'pending', locked_by = NULL, lease_token = NULL, lease_expires_at = NULL,
                last_enqueued_at = @availableAt, updated_at = @now
            WHERE id = @id
            """,
            connection, transaction))
        {
            update.Parameters.AddWithValue("availableAt", now + delay);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("id", importId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ImportSql.EnqueueOutboxAsync(connection, transaction, importId, null, now + delay, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return FinishResult.Done;
    }

    public async Task<FinishResult> FailAsync(Guid importId, Guid leaseToken, string failureCode, string failureReason, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await GuardLeaseAsync(connection, transaction, importId, leaseToken, cancellationToken).ConfigureAwait(false);
        if (state == FinishResult.LeaseLost)
        {
            return state;
        }

        if (state == FinishResult.Cancelled)
        {
            await FinalizeStatusAsync(connection, transaction, importId, ImportStatus.Cancelled, null, null, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return FinishResult.Cancelled;
        }

        await FinalizeStatusAsync(connection, transaction, importId, ImportStatus.Failed, failureCode, failureReason, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return FinishResult.Done;
    }

    public async Task FailUnleasedAsync(Guid importId, string failureCode, string failureReason, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var command = dataSource.CreateCommand(
            """
            UPDATE imports
            SET status = 'failed', failure_code = @code, failure_reason = @reason,
                completed_at = @now, updated_at = @now,
                locked_by = NULL, lease_token = NULL, lease_expires_at = NULL
            WHERE id = @id AND status NOT IN ('completed', 'cancelled', 'failed')
              AND lease_token IS NULL
            """);
        command.Parameters.AddWithValue("code", failureCode);
        command.Parameters.AddWithValue("reason", failureReason);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("id", importId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RecoverStaleAsync(TimeSpan lostNotificationAge, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var recovered = 0;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var stale = new NpgsqlCommand(
            """
            SELECT id, status FROM imports
            WHERE status IN ('processing', 'cancelling')
              AND lease_expires_at IS NOT NULL AND lease_expires_at < @now
            FOR UPDATE SKIP LOCKED
            """,
            connection, transaction))
        {
            stale.Parameters.AddWithValue("now", now);
            var rows = new List<(Guid Id, ImportStatus Status)>();
            await using (var reader = await stale.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((reader.GetGuid(0), ImportSql.FromDb(reader.GetString(1))));
                }
            }

            foreach (var row in rows)
            {
                if (row.Status == ImportStatus.Cancelling)
                {
                    await FinalizeStatusAsync(connection, transaction, row.Id, ImportStatus.Cancelled, null, null, now, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await using var release = new NpgsqlCommand(
                        """
                        UPDATE imports
                        SET status = 'pending', locked_by = NULL, lease_token = NULL, lease_expires_at = NULL,
                            last_enqueued_at = @now, updated_at = @now
                        WHERE id = @id
                        """,
                        connection, transaction);
                    release.Parameters.AddWithValue("now", now);
                    release.Parameters.AddWithValue("id", row.Id);
                    await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await ImportSql.EnqueueOutboxAsync(connection, transaction, row.Id, null, now, cancellationToken).ConfigureAwait(false);
                }

                recovered++;
            }
        }

        await using (var pending = new NpgsqlCommand(
            """
            SELECT id FROM imports
            WHERE status = 'pending'
              AND (last_enqueued_at IS NULL OR last_enqueued_at < @threshold)
            FOR UPDATE SKIP LOCKED
            LIMIT 100
            """,
            connection, transaction))
        {
            pending.Parameters.AddWithValue("threshold", now - lostNotificationAge);
            var ids = new List<Guid>();
            await using (var reader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    ids.Add(reader.GetGuid(0));
                }
            }

            foreach (var id in ids)
            {
                await using var touch = new NpgsqlCommand(
                    "UPDATE imports SET last_enqueued_at = @now, updated_at = @now WHERE id = @id",
                    connection, transaction);
                touch.Parameters.AddWithValue("now", now);
                touch.Parameters.AddWithValue("id", id);
                await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await ImportSql.EnqueueOutboxAsync(connection, transaction, id, null, now, cancellationToken).ConfigureAwait(false);
                recovered++;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recovered;
    }

    private static async Task<FinishResult> GuardLeaseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid importId, Guid leaseToken, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT status FROM imports WHERE id = @id AND lease_token = @leaseToken FOR UPDATE",
            connection, transaction);
        command.Parameters.AddWithValue("id", importId);
        command.Parameters.AddWithValue("leaseToken", leaseToken);
        var status = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return FinishResult.LeaseLost;
        }

        return ImportSql.FromDb((string)status) == ImportStatus.Cancelling ? FinishResult.Cancelled : FinishResult.Done;
    }

    private static async Task FinalizeStatusAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid importId, ImportStatus status,
        string? failureCode, string? failureReason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE imports
            SET status = @status, failure_code = @code, failure_reason = @reason,
                completed_at = @now, updated_at = @now,
                locked_by = NULL, lease_token = NULL, lease_expires_at = NULL
            WHERE id = @id
            """,
            connection, transaction);
        command.Parameters.AddWithValue("status", ImportSql.ToDb(status));
        command.Parameters.AddWithValue("code", (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("reason", (object?)failureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("id", importId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
