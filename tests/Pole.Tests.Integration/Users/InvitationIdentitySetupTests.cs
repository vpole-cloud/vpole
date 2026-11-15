using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pole.Features.Users.Application.Identity;
using Pole.Features.Users.Contracts;
using Xunit;

namespace Pole.Tests.Integration.Users;

public sealed class InvitationIdentitySetupTests
{
    [Theory]
    [InlineData(false, false, false, "VERIFY_EMAIL")]
    [InlineData(true, true, false, "VERIFY_EMAIL")]
    [InlineData(true, false, false, "VERIFY_EMAIL,UPDATE_PASSWORD")]
    public async Task OnlyUnfinishedInvitationIdentityWithoutPasswordCanReceivePasswordSetup(
        bool invitationMarker,
        bool hasPassword,
        bool verified,
        string expectedActions)
    {
        string[]? sent = null;
        var calls = new List<string>();
        using var http = new HttpClient(
            new Handler(async request =>
            {
                calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
                var path = request.RequestUri.AbsolutePath;
                if (path.EndsWith("/token"))
                    return Json(new { access_token = "service-token" });
                if (path.EndsWith("/credentials"))
                    return Json(hasPassword ? new[] { new { type = "password" } } : []);
                if (path.EndsWith("/users"))
                    return Json(
                        new[]
                        {
                            new
                            {
                                id = "subject", email = "invited@example.test", emailVerified = verified,
                                attributes = invitationMarker
                                    ? new Dictionary<string, string[]> { ["pole-invitation-setup"] = ["true"] }
                                    : [],
                                requiredActions = new[] { "UPDATE_PASSWORD" }
                            }
                        });
                if (path.EndsWith("/execute-actions-email"))
                {
                    sent = await request.Content!.ReadFromJsonAsync<string[]>();
                    Assert.DoesNotContain("private-capability", request.RequestUri.AbsoluteUri);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                throw new InvalidOperationException("Unexpected identity request: " + request.Method + " " + path);
            }));
        var provider = new KeycloakIdentityProvider(
            http,
            Options.Create(
                new KeycloakOptions
                {
                    BaseUrl = "https://identity.test", Realm = "pole", AdminClientId = "admin",
                    AdminClientSecret = "test"
                }));
        var result = await provider.StartInvitationAsync(
            new StartInvitedIdentityRequest("invited@example.test", "https://console.test/invitations"),
            CancellationToken.None);
        Assert.True(result is InvitedIdentityStarted);
        Assert.Equal(expectedActions.Split(','), sent);
        Assert.DoesNotContain(calls, call => call.StartsWith("POST ") && call.EndsWith("/users"));
    }

    [Fact]
    public async Task RealmHasOneConfiguredSmtpBlockAndPublicRegistrationRemainsDisabled()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Pole.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        using var realm = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(root.FullName, "infrastructure/keycloak/realms/pole.control-plane.realm.json")));
        Assert.Single(realm.RootElement.EnumerateObject(), property => property.Name == "smtpServer");
        var smtp = realm.RootElement.GetProperty("smtpServer");
        Assert.Equal("${POLE_SMTP_HOST}", smtp.GetProperty("host").GetString());
        Assert.Equal("${POLE_SMTP_PORT}", smtp.GetProperty("port").GetString());
        Assert.False(realm.RootElement.GetProperty("registrationAllowed").GetBoolean());
        var component = realm.RootElement
            .GetProperty("components")
            .GetProperty("org.keycloak.userprofile.UserProfileProvider")[0];
        using var profile = JsonDocument.Parse(
            component.GetProperty("config").GetProperty("kc.user.profile.config")[0].GetString()!);
        var marker = Assert.Single(
            profile.RootElement.GetProperty("attributes").EnumerateArray(),
            attribute => attribute.GetProperty("name").GetString() == "pole-invitation-setup");
        Assert.Equal(
            ["admin"],
            marker.GetProperty("permissions").GetProperty("view").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ["admin"],
            marker.GetProperty("permissions").GetProperty("edit").EnumerateArray().Select(value => value.GetString()));
    }

    private static HttpResponseMessage Json<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class Handler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handle
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handle(request);
    }
}
