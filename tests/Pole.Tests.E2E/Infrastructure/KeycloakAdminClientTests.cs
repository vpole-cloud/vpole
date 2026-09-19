using System.Net;
using System.Text;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed class KeycloakAdminClientTests
{
    [Fact]
    public async Task Expiring_admin_tokens_are_refreshed_before_the_next_operation()
    {
        using var handler = new TokenHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://keycloak.test") };
        var client = new KeycloakAdminClient(http, "test-admin", "test-password");
        await client.SetPasswordAsync("first", "first-password", CancellationToken.None);
        await client.SetPasswordAsync("second", "second-password", CancellationToken.None);
        Assert.Equal(2, handler.TokenRequests);
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        public int TokenRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                TokenRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token-" + TokenRequests + "\",\"expires_in\":1}", Encoding.UTF8, "application/json")
                });
            }
            Assert.Equal("token-" + TokenRequests, request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
