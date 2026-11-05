using System.Net;
using System.Net.Http.Json;
using TransactionImport.IntegrationTests.Infrastructure;

namespace TransactionImport.IntegrationTests.Api;

public sealed class HealthEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Ready_is_healthy_when_dependencies_are_reachable()
    {
        await using var factory = new ApiFactory(postgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>(TestContext.Current.CancellationToken);
        Assert.Equal("Healthy", body!.Status);
        Assert.Equal("Healthy", body.Checks["postgres"]);
        Assert.Equal("Healthy", body.Checks["redis"]);
    }

    [Fact]
    public async Task Ready_is_unavailable_when_database_is_unreachable()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString, postgres.RedisConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_does_not_depend_on_dependencies()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString, ApiFactory.UnreachableRedis);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Every_response_carries_a_server_generated_request_id()
    {
        await using var factory = new ApiFactory(postgres);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Request-Id", "client-supplied");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var requestId = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.NotEqual("client-supplied", requestId);
        Assert.Equal(32, requestId.Length);
    }

    private sealed record HealthBody(string Status, Dictionary<string, string> Checks);
}
