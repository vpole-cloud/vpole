using Pole.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class AuthenticationAndGitHubE2ETests(PoleE2EFixture fixture)
{
    [Fact]
    public async Task Tenant_authorization_blocks_foreign_GitHub_installation_before_the_provider_is_called()
    {
        using var testEvidence = new TestPhaseJournal(nameof(Tenant_authorization_blocks_foreign_GitHub_installation_before_the_provider_is_called)).Activate();
        using var reset = await fixture.Stub.PostAsync("/__e2e/reset", content: null);
        reset.EnsureSuccessStatusCode();

        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/v1/git-provider-connections/github/install",
            fixture.Alpha,
            fixture.Beta.DefaultTenantId);
        request.Content = JsonContent.Create(new { TechnicalName = "github-e2e" });
        using var response = await fixture.Api.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var journal = await fixture.Stub.GetFromJsonAsync<JsonElement[]>("/__e2e/requests");
        Assert.NotNull(journal);
        Assert.Empty(journal);
    }

    [Fact]
    public async Task GitHub_installation_and_OAuth_callback_are_completed_against_the_local_stub()
    {
        using var testEvidence = new TestPhaseJournal(nameof(GitHub_installation_and_OAuth_callback_are_completed_against_the_local_stub)).Activate();
        using var reset = await fixture.Stub.PostAsync("/__e2e/reset", content: null);
        reset.EnsureSuccessStatusCode();

        using var begin = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/v1/git-provider-connections/github/install",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        begin.Content = JsonContent.Create(new { TechnicalName = "github-e2e" });
        using var beginResponse = await fixture.Api.SendAsync(begin);
        beginResponse.EnsureSuccessStatusCode();
        using var beginJson = JsonDocument.Parse(await beginResponse.Content.ReadAsStreamAsync());
        var installationUrl = beginJson.RootElement.GetProperty("installUrl").GetString();
        Assert.False(string.IsNullOrWhiteSpace(installationUrl));

        using var callbackResponse = await fixture.Stub.GetAsync(installationUrl!);
        callbackResponse.EnsureSuccessStatusCode();

        using var connectionsRequest = fixture.CreateAuthenticatedRequest(
            HttpMethod.Get,
            "/api/v1/git-provider-connections",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var connectionsResponse = await fixture.Api.SendAsync(connectionsRequest);
        connectionsResponse.EnsureSuccessStatusCode();
        using var connections = JsonDocument.Parse(await connectionsResponse.Content.ReadAsStreamAsync());
        Assert.Contains(
            connections.RootElement.EnumerateArray(),
            connection => string.Equals(connection.GetProperty("accountLogin").GetString(), "pole-e2e", StringComparison.Ordinal));

        var journal = await fixture.Stub.GetFromJsonAsync<JsonElement[]>("/__e2e/requests");
        Assert.NotNull(journal);
        Assert.Contains(journal, request => request.GetProperty("path").GetString() == "/app/installations/42");
        Assert.Contains(journal, request => request.GetProperty("path").GetString() == "/login/oauth/access_token");
    }
}
