using System.Net;
using System.Text;
using Pole.Cli.Authentication;
using Xunit;

namespace Pole.Cli.Tests;

public sealed class OidcAccessTokenProviderTests
{
    [Fact]
    public async Task Missing_or_expired_login_never_opens_browser_when_interactive_login_is_disabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pole-cli-silent-auth-test-" + Guid.NewGuid().ToString("N"));
        var tokenStore = new CliTokenStore(Path.Combine(directory, "cli", "tokens.json"));
        var issuer = new Uri("https://identity.example/realms/pole");
        var server = new Uri("https://pole.example/");
        var browser = new RecordingBrowser();
        using var http = new HttpClient(
            new StubHandler(request =>
                request.RequestUri!.AbsoluteUri ==
                "https://identity.example/realms/pole/.well-known/openid-configuration"
                    ? Json(
                        "{\"issuer\":\"https://identity.example/realms/pole\",\"authorization_endpoint\":\"https://identity.example/auth\",\"token_endpoint\":\"https://identity.example/token\"}")
                    : new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var provider = new OidcAccessTokenProvider(
            issuer,
            "pole.cli",
            server,
            new OidcAuthenticator(http, browser),
            tokenStore);

        await Assert.ThrowsAsync<CliLoginRequiredException>(() =>
            provider.GetAccessTokenAsync(CancellationToken.None, allowInteractiveLogin: false));
        Assert.Equal(0, browser.OpenCount);

        try
        {
            await tokenStore.SaveAsync(
                issuer,
                "pole.cli",
                server,
                new OidcTokens("expired-access", "revoked-refresh", DateTimeOffset.UtcNow.AddMinutes(-5)),
                CancellationToken.None);
            var expiredProvider = new OidcAccessTokenProvider(
                issuer,
                "pole.cli",
                server,
                new OidcAuthenticator(http, browser),
                tokenStore);
            await Assert.ThrowsAsync<CliLoginRequiredException>(() =>
                expiredProvider.GetAccessTokenAsync(CancellationToken.None, allowInteractiveLogin: false));
            Assert.Equal(0, browser.OpenCount);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingBrowser : IOidcBrowser
    {
        public int OpenCount { get; private set; }

        public void Open(Uri uri) => OpenCount++;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
