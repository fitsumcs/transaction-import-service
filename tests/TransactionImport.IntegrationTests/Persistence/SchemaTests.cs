using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TransactionImport.Infrastructure.Persistence.Migrations;
using TransactionImport.IntegrationTests.Infrastructure;

namespace TransactionImport.IntegrationTests.Persistence;

public sealed class SchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_are_idempotent()
    {
        var applied = await new MigrationRunner(postgres.DataSource, NullLogger<MigrationRunner>.Instance)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task Database_rejects_inconsistent_progress_counters()
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertImportAsync(processed: 10, accepted: 5, rejected: 4));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("imports_progress_consistent", error.ConstraintName);
    }

    [Fact]
    public async Task Database_rejects_a_lease_on_a_pending_import()
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertImportAsync(leaseToken: Guid.NewGuid()));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Same_provider_and_transaction_id_cannot_be_accepted_twice()
    {
        var importId = await InsertImportAsync();
        var transactionId = $"txn-{Guid.NewGuid():N}";
        await InsertTransactionAsync(importId, transactionId);

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertTransactionAsync(importId, transactionId));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }

    private async Task<Guid> InsertImportAsync(long processed = 0, long accepted = 0, long rejected = 0, Guid? leaseToken = null)
    {
        var id = Guid.CreateVersion7();
        await using var command = postgres.DataSource.CreateCommand(
            """
            INSERT INTO imports (id, provider_id, status, file_name, storage_key, file_size_bytes, file_sha256,
                                 processed_count, accepted_count, rejected_count, max_attempts,
                                 locked_by, lease_token, lease_expires_at, created_at, updated_at)
            VALUES (@id, 'provider-schema', 'pending', 'f.ndjson', 'k', 0, repeat('a', 64),
                    @processed, @accepted, @rejected, 3,
                    @lockedBy, @leaseToken, @leaseExpiresAt, now(), now())
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("processed", processed);
        command.Parameters.AddWithValue("accepted", accepted);
        command.Parameters.AddWithValue("rejected", rejected);
        command.Parameters.AddWithValue("lockedBy", leaseToken is null ? DBNull.Value : "worker");
        command.Parameters.AddWithValue("leaseToken", leaseToken is null ? DBNull.Value : leaseToken.Value);
        command.Parameters.AddWithValue("leaseExpiresAt", leaseToken is null ? DBNull.Value : DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task InsertTransactionAsync(Guid importId, string transactionId)
    {
        await using var command = postgres.DataSource.CreateCommand(
            """
            INSERT INTO transactions (provider_id, transaction_id, import_id, line_number, account_id, merchant_id,
                                      amount, currency, occurred_at, fingerprint, fingerprint_version,
                                      risk_score, risk_level, created_at)
            VALUES ('provider-schema', @transactionId, @importId, 1, 'acc', 'mer', 10, 'USD', now(),
                    decode(repeat('ab', 32), 'hex'), 1, 10, 'low', now())
            """);
        command.Parameters.AddWithValue("transactionId", transactionId);
        command.Parameters.AddWithValue("importId", importId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
