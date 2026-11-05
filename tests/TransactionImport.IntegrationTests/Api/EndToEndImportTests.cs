using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using TransactionImport.Infrastructure.Persistence;
using TransactionImport.IntegrationTests.Infrastructure;

namespace TransactionImport.IntegrationTests.Api;

/// <summary>
/// Full API + Worker path against real Postgres and Redis: upload → outbox → stream → process → query.
/// Each test uses an isolated Redis stream + shared storage root so parallel workers cannot steal jobs.
/// </summary>
public sealed class EndToEndImportTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Upload_processes_to_completed_with_summary_and_paginated_rejections()
    {
        await using var pair = CreatePair();
        using var client = pair.Api.CreateClient();

        var ndjson = string.Join(
            '\n',
            ValidLine("txn-e2e-1", "USD", 10.5m),
            ValidLine("txn-e2e-1", "USD", 10.5m), // duplicate
            """{"transactionId":"bad","accountId":"a","merchantId":"m","amount":-1,"currency":"USD","timestamp":"2026-07-20T10:25:00Z"}""",
            ValidLine("txn-e2e-2", "EUR", 20m),
            "{not-json") + "\n";

        var created = await UploadAsync(client, $"key-{Guid.NewGuid():N}", "provider-e2e", ndjson);
        var status = await WaitForTerminalAsync(client, created.Id, TimeSpan.FromSeconds(60));

        Assert.Equal("completed", status.Status);
        Assert.True(status.Progress.Accepted >= 2, $"accepted={status.Progress.Accepted}");
        Assert.True(status.Progress.Rejected >= 2, $"rejected={status.Progress.Rejected} duplicates={status.Progress.Duplicates}");
        Assert.Equal(status.Progress.Processed, status.Progress.Accepted + status.Progress.Rejected);

        var summary = await client.GetFromJsonAsync<SummaryResponse>(
            $"/v1/imports/{created.Id}/summary",
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(created.Id, summary.ImportId);
        Assert.True(summary.IsFinal);
        Assert.Equal(status.Progress.Accepted, summary.Totals.Accepted);
        Assert.Contains(summary.ByCurrency, c => c.Currency is "USD" or "EUR");
        Assert.True(
            summary.ByRiskLevel.Low + summary.ByRiskLevel.Medium + summary.ByRiskLevel.High
            >= summary.Totals.Accepted);

        var page1 = await client.GetFromJsonAsync<RejectionPageDto>(
            $"/v1/imports/{created.Id}/rejections?limit=1",
            TestContext.Current.CancellationToken);
        Assert.NotNull(page1);
        Assert.Single(page1.Items);
        Assert.False(string.IsNullOrWhiteSpace(page1.NextCursor));

        var page2 = await client.GetFromJsonAsync<RejectionPageDto>(
            $"/v1/imports/{created.Id}/rejections?limit=50&cursor={Uri.EscapeDataString(page1.NextCursor!)}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(page2);
        Assert.NotEmpty(page2.Items);
        Assert.DoesNotContain(page2.Items, i => i.LineNumber == page1.Items[0].LineNumber);

        var allRejections = page1.Items.Concat(page2.Items).ToArray();
        Assert.Contains(allRejections, r => r.Reason.Contains("DUPLICATE", StringComparison.OrdinalIgnoreCase)
            || r.Reason.Contains("INVALID", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Failed_import_can_be_retried_and_completes()
    {
        await using var pair = CreatePair();
        using var client = pair.Api.CreateClient();

        var created = await UploadAsync(
            client,
            $"key-{Guid.NewGuid():N}",
            "provider-retry",
            ValidLine("txn-retry-1", "USD", 1m) + "\n");

        await WaitUntilAsync(
            async () =>
            {
                var s = await client.GetFromJsonAsync<StatusResponse>($"/v1/imports/{created.Id}", TestContext.Current.CancellationToken);
                return s is not null && s.Status is not "pending";
            },
            TimeSpan.FromSeconds(30));

        var current = await client.GetFromJsonAsync<StatusResponse>($"/v1/imports/{created.Id}", TestContext.Current.CancellationToken);
        if (current!.Status == "completed")
        {
            var notRetryable = await client.PostAsync(
                new Uri($"/v1/imports/{created.Id}/retry", UriKind.Relative),
                null,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, notRetryable.StatusCode);
        }

        Guid failedId;
        await using (var conn = await postgres.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO imports (
                  id, provider_id, status, file_name, storage_key, file_size_bytes, file_sha256,
                  attempt_count, max_attempts, failure_code, failure_reason,
                  processed_count, accepted_count, rejected_count, duplicate_count,
                  checkpoint_byte_offset, checkpoint_line_number,
                  created_at, completed_at, updated_at)
                SELECT gen_random_uuid(), @retryProvider, 'failed', file_name, storage_key, file_size_bytes, file_sha256,
                       3, 3, 'TEST_FAIL', 'forced for retry test',
                       0, 0, 0, 0, 0, 0, NOW(), NOW(), NOW()
                FROM imports WHERE id = @id
                RETURNING id
                """,
                conn);
            cmd.Parameters.AddWithValue("id", Guid.ParseExact(created.Id, "N"));
            // Fresh provider so the same transactionId can be accepted again after retry.
            cmd.Parameters.AddWithValue("retryProvider", $"provider-retry-{Guid.NewGuid():N}"[..32]);
            failedId = (Guid)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        var retryResponse = await client.PostAsync(
            new Uri($"/v1/imports/{failedId:N}/retry", UriKind.Relative),
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        var retryBody = await retryResponse.Content.ReadFromJsonAsync<RetryResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("pending", retryBody!.Status);

        var terminal = await WaitForTerminalAsync(client, failedId.ToString("N"), TimeSpan.FromSeconds(60));
        Assert.Equal("completed", terminal.Status);
        Assert.True(terminal.Progress.Accepted >= 1);
    }

    [Fact]
    public async Task Stale_lease_recovery_requeues_processing_import()
    {
        var storage = Path.Combine(Path.GetTempPath(), "transaction-import-e2e", Guid.NewGuid().ToString("N"));
        var suffix = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(storage);

        await using var api = new ApiFactory(postgres, storage, suffix);
        using var client = api.CreateClient();

        var created = await UploadAsync(
            client,
            $"key-{Guid.NewGuid():N}",
            "provider-recover",
            ValidLine("txn-recover-1", "USD", 3m) + "\n");
        var importId = Guid.ParseExact(created.Id, "N");

        await using (var conn = await postgres.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = new NpgsqlCommand(
                """
                UPDATE imports
                SET status = 'processing',
                    locked_by = 'dead-worker',
                    lease_token = gen_random_uuid(),
                    lease_expires_at = NOW() - INTERVAL '1 minute',
                    started_at = COALESCE(started_at, NOW()),
                    updated_at = NOW()
                WHERE id = @id
                """,
                conn);
            cmd.Parameters.AddWithValue("id", importId);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var store = new PostgresImportProcessingStore(postgres.DataSource, TimeProvider.System);
        var recovered = await store.RecoverStaleAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(recovered >= 1);

        await using (var conn = await postgres.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = new NpgsqlCommand("SELECT status, lease_token FROM imports WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", importId);
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("pending", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
        }

        await using var worker = new WorkerFactory(postgres, storage, suffix);
        using var _ = worker.CreateClient();
        var terminal = await WaitForTerminalAsync(client, created.Id, TimeSpan.FromSeconds(60));
        Assert.Equal("completed", terminal.Status);
    }

    private HostPair CreatePair()
    {
        var storage = Path.Combine(Path.GetTempPath(), "transaction-import-e2e", Guid.NewGuid().ToString("N"));
        var suffix = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(storage);
        var api = new ApiFactory(postgres, storage, suffix);
        var worker = new WorkerFactory(postgres, storage, suffix);
        _ = worker.CreateClient(); // start outbox relay + consumer
        return new HostPair(api, worker);
    }

    private sealed class HostPair(ApiFactory api, WorkerFactory worker) : IAsyncDisposable
    {
        public ApiFactory Api { get; } = api;

        public async ValueTask DisposeAsync()
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            await Api.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<StatusResponse> WaitForTerminalAsync(HttpClient client, string id, TimeSpan timeout)
    {
        StatusResponse? last = null;
        await WaitUntilAsync(
            async () =>
            {
                last = await client.GetFromJsonAsync<StatusResponse>($"/v1/imports/{id}", TestContext.Current.CancellationToken);
                return last is not null && last.Status is "completed" or "failed" or "cancelled";
            },
            timeout);
        return last!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Condition not met within {timeout}.");
    }

    private static string ValidLine(string id, string currency, decimal amount) =>
        $$"""{"transactionId":"{{id}}","accountId":"acc-1","merchantId":"merchant-1","amount":{{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"currency":"{{currency}}","timestamp":"2026-07-20T10:25:00Z","description":"ok"}""";

    private static async Task<CreateResponse> UploadAsync(HttpClient client, string key, string provider, string ndjson)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(ndjson));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        content.Add(file, "file", "sample.ndjson");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/imports") { Content = content };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Provider-Id", provider);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateResponse>(TestContext.Current.CancellationToken))!;
    }

    private sealed record CreateResponse(string Id, string Status, DateTimeOffset CreatedAt);
    private sealed record StatusResponse(string Id, string Status, Progress Progress, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? FailureReason);
    private sealed record Progress(long Processed, long Accepted, long Rejected, long Duplicates);
    private sealed record RetryResponse(string Id, string Status);
    private sealed record SummaryResponse(
        string ImportId,
        TotalsDto Totals,
        IReadOnlyList<CurrencyTotalDto> ByCurrency,
        RiskLevelDto ByRiskLevel,
        IReadOnlyList<GroupTotalDto> TopMerchants,
        IReadOnlyList<GroupTotalDto> TopAccounts,
        bool IsFinal);
    private sealed record TotalsDto(long Accepted, long Rejected, long Duplicates);
    private sealed record CurrencyTotalDto(string Currency, long TransactionCount, decimal TotalAmount);
    private sealed record RiskLevelDto(long Low, long Medium, long High);
    private sealed record GroupTotalDto(string Id, string Currency, long TransactionCount, decimal TotalAmount);
    private sealed record RejectionPageDto(IReadOnlyList<RejectionItemDto> Items, string? NextCursor);
    private sealed record RejectionItemDto(long LineNumber, string Reason, string Message, JsonElement? RawValue);
}
