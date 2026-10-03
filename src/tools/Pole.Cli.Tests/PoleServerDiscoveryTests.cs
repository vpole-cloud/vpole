using System.Net;
using System.Text;
using Pole.Cli.Authentication;
using Xunit;

namespace Pole.Cli.Tests;

public sealed class PoleServerDiscoveryTests
{
    [Fact]
    public async Task Reads_public_oidc_settings_from_selected_server_origin()
    {
        HttpRequestMessage? observed = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            return Json("{\"issuer\":\"https://identity.example/realms/pole\",\"clientId\":\"pole.cli\"}");
        }));
        var discovery = new PoleServerDiscovery(http);

        var identity = await discovery.DiscoverAsync(new Uri("https://pole.example:8443/"), CancellationToken.None);

        Assert.Equal("https://pole.example:8443/.well-known/pole-cli", observed!.RequestUri!.AbsoluteUri);
        Assert.Equal("https://identity.example/realms/pole", identity.Issuer.AbsoluteUri);
        Assert.Equal("pole.cli", identity.ClientId);
    }

    [Fact]
    public async Task Rejects_http_identity_issuer_outside_loopback_development()
    {
        using var http = new HttpClient(new StubHandler(_ => Json(
            "{\"issuer\":\"http://identity.example/realms/pole\",\"clientId\":\"pole.cli\"}")));
        var discovery = new PoleServerDiscovery(http);

        await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.DiscoverAsync(
            new Uri("https://pole.example/"), CancellationToken.None));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
