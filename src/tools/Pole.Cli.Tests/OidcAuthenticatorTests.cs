using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Pole.Cli.Authentication;
using Xunit;

namespace Pole.Cli.Tests;

public sealed class OidcAuthenticatorTests
{
    [Fact]
    public void Authorization_request_uses_state_and_S256_pkce()
    {
        var authorizationUri = OidcAuthenticator.BuildAuthorizationUri(
            new Uri("https://identity.example/authorize"),
            "pole.cli",
            OidcAuthenticator.RedirectUri,
            "state-value",
            "challenge-value");
        var query = System.Web.HttpUtility.ParseQueryString(authorizationUri.Query);

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("state-value", query["state"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("challenge-value", query["code_challenge"]);
        Assert.Equal(OidcAuthenticator.RedirectUri.AbsoluteUri, query["redirect_uri"]);
    }

    [Fact]
    public async Task Callback_rejects_wrong_state_then_accepts_valid_state()
    {
        using var listener = new HttpListener();
        var port = ReservePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wait = OidcAuthenticator.WaitForAuthorizationCodeAsync(listener, "expected-state", cancellation.Token);
        using var client = new HttpClient();
        using var rejected = await client.GetAsync(prefix + "callback?code=attacker&state=wrong-state&error=access_denied", cancellation.Token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var accepted = await client.GetAsync(prefix + "callback?code=good-code&state=expected-state", cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("good-code", await wait);
    }

    [Fact]
    public void Login_callback_is_fixed_loopback_and_non_loopback_http_is_rejected()
    {
        Assert.Equal("127.0.0.1", OidcAuthenticator.RedirectUri.Host);
        Assert.Equal("/callback", OidcAuthenticator.RedirectUri.AbsolutePath);
        Assert.Equal(48763, OidcAuthenticator.RedirectUri.Port);
        Assert.False(OidcAuthenticator.IsSecureEndpoint(new Uri("http://identity.example/token"), new Uri("http://localhost/realm")));
        Assert.True(OidcAuthenticator.IsSecureEndpoint(new Uri("http://127.0.0.1:8080/token"), new Uri("http://localhost/realm")));
    }

    [Fact]
    public void Pkce_challenge_matches_sha256_of_verifier()
    {
        var verifier = "verifier-123";
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, OidcAuthenticator.CreateCodeChallenge(verifier));
    }

    [Fact]
    public async Task Oidc_http_client_does_not_follow_redirects()
    {
        using var source = new HttpListener();
        using var destination = new HttpListener();
        var sourcePort = ReservePort();
        var destinationPort = ReservePort();
        source.Prefixes.Add($"http://127.0.0.1:{sourcePort}/");
        destination.Prefixes.Add($"http://127.0.0.1:{destinationPort}/");
        source.Start();
        destination.Start();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var http = OidcAuthenticator.CreateHttpClient();
        var request = http.GetAsync($"http://127.0.0.1:{sourcePort}/discovery", cancellation.Token);
        var destinationRequest = destination.GetContextAsync();
        var context = await source.GetContextAsync().WaitAsync(cancellation.Token);
        using (context.Response)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Redirect;
            context.Response.RedirectLocation = $"http://127.0.0.1:{destinationPort}/steal";
            context.Response.Close();
        }

        using var response = await request;
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await Task.Delay(150, cancellation.Token);
        Assert.False(destinationRequest.IsCompleted);
        destination.Stop();
        await Assert.ThrowsAnyAsync<Exception>(async () => await destinationRequest);
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
