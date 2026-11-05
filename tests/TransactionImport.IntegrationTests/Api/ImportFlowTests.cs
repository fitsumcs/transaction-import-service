using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using TransactionImport.IntegrationTests.Infrastructure;

namespace TransactionImport.IntegrationTests.Api;

public sealed class ImportFlowTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Create_import_returns_202_and_persists_pending_status()
    {
        await using var factory = new ApiFactory(postgres);
        using var client = factory.CreateClient();

        var created = await UploadAsync(client, $"key-{Guid.NewGuid():N}", "provider-create", ValidLine("txn-create-1"));
        Assert.Equal("pending", created.Status);

        var status = await client.GetFromJsonAsync<StatusResponse>($"/v1/imports/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal("pending", status!.Status);
        Assert.Equal(0, status.Progress.Processed);
    }

    [Fact]
    public async Task Idempotent_replay_returns_the_same_import_without_a_second_file()
    {
        await using var factory = new ApiFactory(postgres);
        using var client = factory.CreateClient();
        var key = $"key-{Guid.NewGuid():N}";

        var first = await UploadAsync(client, key, "provider-idem", ValidLine("txn-idem-1"));
        var second = await UploadAsync(client, key, "provider-idem", ValidLine("txn-idem-1"));

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_idempotency_key_create_one_import()
    {
        await using var factory = new ApiFactory(postgres);
        using var client = factory.CreateClient();
        var key = $"concurrent-{Guid.NewGuid():N}";
        var line = ValidLine("txn-concurrent");

        async Task<(HttpStatusCode Status, string? Id)> UploadOnceAsync()
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(line + "\n")), "file", "sample.ndjson");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/imports") { Content = content };
            request.Headers.Add("Idempotency-Key", key);
            request.Headers.Add("X-Provider-Id", "provider-concurrent");
            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                var body = await response.Content.ReadFromJsonAsync<CreateResponse>(TestContext.Current.CancellationToken);
                return (response.StatusCode, body!.Id);
            }

            return (response.StatusCode, null);
        }

        var results = await Task.WhenAll(UploadOnceAsync(), UploadOnceAsync(), UploadOnceAsync());
        var accepted = results.Where(r => r.Status == HttpStatusCode.Accepted).Select(r => r.Id).ToArray();
        Assert.Single(accepted);
        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.Accepted or HttpStatusCode.Conflict));

        // After the winner finishes, a replay returns the same import.
        var replay = await UploadAsync(client, key, "provider-concurrent", line);
        Assert.Equal(accepted[0], replay.Id);
    }

    [Fact]
    public async Task Cancel_pending_import_is_immediate()
    {
        await using var factory = new ApiFactory(postgres);
        using var client = factory.CreateClient();

        var created = await UploadAsync(client, $"key-{Guid.NewGuid():N}", "provider-cancel", ValidLine("txn-cancel-1"));
        var response = await client.PostAsync(new Uri($"/v1/imports/{created.Id}/cancel", UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CancelResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("cancelled", body!.Status);
    }

    [Fact]
    public async Task Missing_provider_or_idempotency_key_is_rejected()
    {
        await using var factory = new ApiFactory(postgres);
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(ValidLine("t"))), "file", "f.ndjson");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/imports") { Content = content };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string ValidLine(string id) =>
        $$"""{"transactionId":"{{id}}","accountId":"acc-1","merchantId":"merchant-1","amount":10.5,"currency":"USD","timestamp":"2026-07-20T10:25:00Z","description":"ok"}""";

    private static async Task<CreateResponse> UploadAsync(HttpClient client, string key, string provider, string ndjson)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(ndjson + "\n"));
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
    private sealed record CancelResponse(string Id, string Status);
}
