using System.Net;
using System.Text;
using Pole.Cli.Api;
using Pole.Cli.Authentication;
using Xunit;

namespace Pole.Cli.Tests;

public sealed class CliCommandRunnerTests
{
    [Theory]
    [InlineData("orders", "name=orders")]
    [InlineData("orders/2026", "name=orders%2F2026")]
    public async Task Forward_without_an_eligible_route_does_not_start_browser_login(string name, string expectedQuery)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "pole-cli-forward-command-test-" + Guid.NewGuid().ToString("N"));
        var server = new Uri("https://pole.example/");
        var identity = new PoleServerIdentity(new Uri("https://identity.example/realms/pole"), "pole.cli");
        var tokenStore = new CliTokenStore(Path.Combine(directory, "cli", "tokens.json"));
        var accessToken = Jwt("account-one");
        var accountKey = CliAccountIdentity.GetAccountKey(accessToken, identity.Issuer, server);
        var browser = new RecordingBrowser();
        var matchedQuery = false;
        using var http = new HttpClient(
            new StubHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/tenants" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { new TenantDto("tenant-1", "Tenant One", "Owner", true) }),
                },
                "/api/v1/private-access/resources/postgres/grants" when request.RequestUri.Query.Contains(
                    expectedQuery,
                    StringComparison.Ordinal) && (matchedQuery = true) => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { items = Array.Empty<object>() }),
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
        try
        {
            await tokenStore.SaveProfileAsync(server, identity, CancellationToken.None);
            await tokenStore.SaveAsync(
                identity.Issuer,
                identity.ClientId,
                server,
                new OidcTokens(accessToken, null, DateTimeOffset.UtcNow.AddMinutes(10)),
                CancellationToken.None);
            await tokenStore.SaveActiveTenantAsync(accountKey, "tenant-1", CancellationToken.None);
            var runner = new CliCommandRunner(http, tokenStore, new OidcAuthenticator(http, browser));

            var result = await runner.RunAsync(["forward", $"postgres/{name}"]);

            Assert.Equal(1, result);
            Assert.Equal(0, browser.OpenCount);
            Assert.True(matchedQuery);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static string Jwt(string subject)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"sub\":\"{subject}\"}}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"header.{payload}.signature";
    }

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
