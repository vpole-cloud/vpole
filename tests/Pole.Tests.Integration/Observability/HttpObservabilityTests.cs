using Pole.Observability;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Observability;

public sealed class HttpObservabilityTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Request_ReturnsProvidedCorrelationId_AndTraceId()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/resource-groups?pageNumber=1&pageSize=1");
        request.Headers.Add(TraceHeaders.CorrelationId, "support-ticket-123");

        var response = await Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal("support-ticket-123", SingleHeader(response, TraceHeaders.CorrelationId));
        Assert.Matches("^[0-9a-f]{32}$", SingleHeader(response, TraceHeaders.TraceId));
    }

    [Fact]
    public async Task Request_GeneratesCorrelationId_WhenHeaderIsMissing()
    {
        var response = await Client.GetAsync("/api/v1/resource-groups?pageNumber=1&pageSize=1");

        response.EnsureSuccessStatusCode();
        Assert.Matches("^[0-9a-f]{32}$", SingleHeader(response, TraceHeaders.CorrelationId));
        Assert.Matches("^[0-9a-f]{32}$", SingleHeader(response, TraceHeaders.TraceId));
    }

    [Fact]
    public async Task Cors_ExposesTraceHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/resource-groups?pageNumber=1&pageSize=1");
        request.Headers.Add("Origin", "http://localhost:5173");

        var response = await Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var exposedHeaders = SingleHeader(response, "Access-Control-Expose-Headers");
        Assert.Contains(TraceHeaders.CorrelationId, exposedHeaders);
        Assert.Contains(TraceHeaders.TraceId, exposedHeaders);
    }

    private static string SingleHeader(HttpResponseMessage response, string headerName)
    {
        Assert.True(response.Headers.TryGetValues(headerName, out var values), $"Missing header '{headerName}'.");
        return Assert.Single(values);
    }
}
