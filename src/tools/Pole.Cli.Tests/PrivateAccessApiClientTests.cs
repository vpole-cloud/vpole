using System.Net;
using System.Net.Http.Json;
using Pole.Cli.Api;
using Xunit;

namespace Pole.Cli.Tests;

public sealed class PrivateAccessApiClientTests
{
    [Fact]
    public async Task Tenant_list_uses_bearer_without_a_tenant_header()
    {
        HttpRequestMessage? observed = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new TenantDto("tenant-1", "Tenant One", "Owner", true) })
            };
        }));
        var client = new PrivateAccessApiClient(http, new Uri("https://pole.example"), _ => Task.FromResult("access-jwt"));

        var tenants = await client.GetTenantsAsync(CancellationToken.None);

        Assert.Single(tenants);
        Assert.Equal("https://pole.example/api/v1/tenants", observed!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer access-jwt", observed.Headers.Authorization!.ToString());
        Assert.DoesNotContain("X-Pole-Tenant-ID", observed.Headers.Select(header => header.Key));
    }

    [Fact]
    public async Task Resource_grant_resolution_uses_kind_name_and_exact_optional_filters()
    {
        HttpRequestMessage? observed = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    items = new[]
                    {
                        new PrivateAccessResourceGrantDto("grant-1", "deployment-1", "primary", DateTimeOffset.UtcNow.AddHours(1))
                    }
                })
            };
        }));
        var client = new PrivateAccessApiClient(http, new Uri("https://pole.example"), _ => Task.FromResult("access-jwt"));

        var grants = await client.GetResourceGrantsAsync(
            "tenant-1", "postgres", "orders", "deployment-1", "primary", CancellationToken.None);

        Assert.Single(grants);
        Assert.Equal("https://pole.example/api/v1/private-access/resources/postgres/grants?name=orders&deploymentId=deployment-1&endpointId=primary", observed!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer access-jwt", observed.Headers.Authorization!.ToString());
        Assert.Equal("tenant-1", Assert.Single(observed.Headers.GetValues("X-Pole-Tenant-ID")));
    }

    [Fact]
    public async Task Grant_list_uses_bearer_and_selected_tenant_headers()
    {
        HttpRequestMessage? observed = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[]
                {
                    new PrivateAccessGrantDto("grant-1", "tenant-1", "subject-1", "Redis", "redis-1", "deployment-1", "primary", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), false)
                })
            };
        }));
        var client = new PrivateAccessApiClient(http, new Uri("https://pole.example"), _ => Task.FromResult("access-jwt"));

        var grants = await client.GetGrantsAsync("tenant-1", CancellationToken.None);

        Assert.Single(grants);
        Assert.Equal(HttpMethod.Get, observed!.Method);
        Assert.Equal("https://pole.example/api/v1/private-access/grants", observed.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer access-jwt", observed.Headers.Authorization!.ToString());
        Assert.Equal("tenant-1", Assert.Single(observed.Headers.GetValues("X-Pole-Tenant-ID")));
    }

    [Fact]
    public async Task Session_request_returns_one_use_ticket_without_putting_it_in_the_url()
    {
        HttpRequestMessage? observed = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new PrivateAccessSessionDto("session-1", "opaque-one-use-ticket", DateTimeOffset.UtcNow.AddSeconds(30)))
            };
        }));
        var client = new PrivateAccessApiClient(http, new Uri("https://pole.example"), _ => Task.FromResult("access-jwt"));

        var session = await client.CreateSessionAsync("tenant-1", "grant/1", CancellationToken.None);

        Assert.Equal("opaque-one-use-ticket", session.Ticket);
        Assert.Equal("https://pole.example/api/v1/private-access/grants/grant%2F1/sessions", observed!.RequestUri!.AbsoluteUri);
        Assert.False(observed.RequestUri.Query.Contains("ticket", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpMethod.Post, observed.Method);
        Assert.Equal("Bearer access-jwt", observed.Headers.Authorization!.ToString());
        Assert.Equal("tenant-1", Assert.Single(observed.Headers.GetValues("X-Pole-Tenant-ID")));
    }

    [Fact]
    public async Task Expired_session_ticket_is_rejected()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new PrivateAccessSessionDto("session-1", "spent-ticket", DateTimeOffset.UtcNow.AddSeconds(-1)))
        }));
        var client = new PrivateAccessApiClient(http, new Uri("https://pole.example"), _ => Task.FromResult("access-jwt"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateSessionAsync("tenant-1", "grant-1", CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
