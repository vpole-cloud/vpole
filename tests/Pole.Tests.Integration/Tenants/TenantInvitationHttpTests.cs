using System.Collections.Immutable;
using System.Net;
using Pole.Tests.Integration.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Endpoints.Invitations;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Contracts;
using Pole.Features.Users.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

[Collection(IntegrationTestCollection.Global3)]
public sealed class TenantInvitationHttpTests(
    MongoReplicaSetFixture fixture
) : IsolatedApiIntegrationTestBase(fixture)
{
    private int _setupCalls;

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddSingleton<IOptions<InvitationOptions>>(
            Options.Create(
                new InvitationOptions
                {
                    FrontendUrl = "https://console.example.test",
                }));
        services.AddSingleton<StartInvitedIdentity>((_, _) =>
        {
            _setupCalls++;
            return Task.FromResult<StartInvitedIdentityOutcome>(new InvitedIdentityStarted("recipient", true));
        });
    }

    [Fact]
    public async Task PreviewAndExplicitOnboardingNeverGrantAccessAndAcceptanceNeedsVerifiedMatchingIdentity()
    {
        var token = await CreateInvitation();
        Client.DefaultRequestHeaders.Remove(TestAuthenticationHandler.UserHeader);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        using var preview = await Client.PostAsJsonAsync(
            "/api/v1/tenant-invitations/preview",
            new InvitationTokenBody(token));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(0, _setupCalls);
        using var get = await Client.GetAsync("/api/v1/tenant-invitations/accept");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using var setup = await Client.PostAsJsonAsync(
            "/api/v1/tenant-invitations/onboarding",
            new InvitationTokenBody(token));
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Equal(1, _setupCalls);
        Assert.False(await Fixture.DbContext.Users.Find(item => item.Id == "recipient").AnyAsync());
        Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, "recipient");
        Client.DefaultRequestHeaders.Remove(TestAuthenticationHandler.EmailHeader);
        Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.EmailHeader, "other@example.test");
        Client.DefaultRequestHeaders.Add("X-Test-Email-Verified", "true");
        using var wrong = await Client.PostAsJsonAsync(
            "/api/v1/tenant-invitations/accept",
            new InvitationTokenBody(token));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Client.DefaultRequestHeaders.Remove(TestAuthenticationHandler.EmailHeader);
        Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.EmailHeader, "invited@example.test");
        Client.DefaultRequestHeaders.Remove("X-Test-Email-Verified");
        using var unverified = await Client.PostAsJsonAsync(
            "/api/v1/tenant-invitations/accept",
            new InvitationTokenBody(token));
        Assert.Equal(HttpStatusCode.Forbidden, unverified.StatusCode);
        Client.DefaultRequestHeaders.Add("X-Test-Email-Verified", "true");
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, "an-invalid-active-tenant");
        using var accepted = await Client.PostAsJsonAsync(
            "/api/v1/tenant-invitations/accept",
            new InvitationTokenBody(token));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var response = await accepted.Content.ReadFromJsonAsync<InvitationAcceptedDto>();
        Assert.Equal(Data.TenantId, response!.TenantId);
        Assert.Equal(Data.TenantId, response.DefaultTenantId);
        var memberships = await Fixture.DbContext
            .Tenants
            .Find(item => item.Members.Any(member => member.UserId == "recipient"))
            .ToListAsync();
        Assert.Equal(Data.TenantId, Assert.Single(memberships).Id);
    }

    [Fact]
    public async Task InvitationManagementUsesRouteTenantAndNeverLeaksOtherTenantInvitations()
    {
        await CreateInvitation();
        var current = await Fixture.DbContext.Tenants.Find(item => item.Id == Data.TenantId).SingleAsync();
        var other = TenantFactory.Create("Other tenant");
        other = other with
        {
            Members =
            [
                TenantFactory.CreateMember(other.Id, "other-owner", "private@example.test", TenantRole.Owner)
            ]
        };
        await Fixture.DbContext.Tenants.InsertOneAsync(other);
        using var crossTenant = await Client.GetAsync($"/api/v1/tenants/{other.Id}/invitations");
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
        Assert.DoesNotContain("private@example.test", await crossTenant.Content.ReadAsStringAsync());
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, "invalid-active-header");
        using var own = await Client.GetAsync($"/api/v1/tenants/{current.Id}/invitations");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var invitation = Assert.Single((await own.Content.ReadFromJsonAsync<InvitationDto[]>())!);
        using var wrongRoute = await Client.PostAsync(
            $"/api/v1/tenants/{other.Id}/invitations/{invitation.Id}/revoke",
            null);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRoute.StatusCode);
    }

    [Theory]
    [InlineData(TenantRole.Viewer, TenantRole.Member)]
    [InlineData(TenantRole.Member, TenantRole.Viewer)]
    [InlineData(TenantRole.Admin, TenantRole.Owner)]
    public async Task HttpGrantChecksTenantRoleEvenForPlatformAdministrator(TenantRole role, TenantRole grant)
    {
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == Data.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(
            item => item.Id == tenant.Id,
            tenant with { Members = tenant.Members.Select(member => member with { Role = role }).ToImmutableList() });
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        using var result = await Client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant.Id}/invitations",
            new CreateTenantInvitationBody("invited@example.test", grant));
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlatformAdminCannotReadInvitationEmailsWithoutLiveTenantManagementRole(bool outsider)
    {
        await CreateInvitation();
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == Data.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(
            item => item.Id == tenant.Id,
            tenant with
            {
                Members = tenant.Members.Select(member => member with { Role = TenantRole.Viewer }).ToImmutableList()
            });
        if (outsider)
        {
            Client.DefaultRequestHeaders.Remove(TestAuthenticationHandler.UserHeader);
            Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, "unjoined-platform-admin");
        }

        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        using var response = await Client.GetAsync($"/api/v1/tenants/{tenant.Id}/invitations");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("invited@example.test", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("revoke", false)]
    [InlineData("revoke", true)]
    [InlineData("resend", false)]
    [InlineData("resend", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    public async Task ManagementChecksLiveAuthorityBeforeInvitationExistence(string operation, bool outsider)
    {
        await CreateInvitation();
        var invitation = await Fixture.Database
            .GetCollection<TenantInvitation>("TenantInvitations")
            .Find(item => item.TenantId == Data.TenantId)
            .SingleAsync();
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == Data.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(
            item => item.Id == tenant.Id,
            tenant with
            {
                Members = tenant.Members.Select(member => member with { Role = TenantRole.Viewer }).ToImmutableList()
            });
        if (outsider)
        {
            Client.DefaultRequestHeaders.Remove(TestAuthenticationHandler.UserHeader);
            Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, "outside-admin");
        }

        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        foreach (var id in new[] { invitation.Id, new string('A', 64) })
        {
            var path = $"/api/v1/tenants/{tenant.Id}/invitations/{id}";
            using var response = operation == "update"
                ? await Client.PutAsJsonAsync(path, new UpdateTenantInvitationBody(TenantRole.Member))
                : await Client.PostAsync(path + "/" + operation, null);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var retained = await Fixture.Database
            .GetCollection<TenantInvitation>("TenantInvitations")
            .Find(item => item.Id == invitation.Id)
            .SingleAsync();
        Assert.Equal(invitation.Revision, retained.Revision);
        Assert.Equal(invitation.Status, retained.Status);
        Assert.Equal(invitation.Role, retained.Role);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptanceReturnsSafeIdentityConflictAndLeavesInvitationUnconsumed(bool subjectCollision)
    {
        var token = await CreateInvitation();
        var existing = PoleUserFactory.Create(
            subjectCollision ? "recipient" : "existing-recipient",
            subjectCollision ? "old@example.test" : "invited@example.test",
            null,
            null,
            "",
            DateTime.UtcNow);
        await Fixture.DbContext.Users.InsertOneAsync(existing);
        Client.DefaultRequestHeaders.Remove(TestAuthenticationHandler.UserHeader);
        Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, "recipient");
        Client.DefaultRequestHeaders.Remove(TestAuthenticationHandler.EmailHeader);
        Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.EmailHeader, "invited@example.test");
        Client.DefaultRequestHeaders.Add("X-Test-Email-Verified", "true");
        using var response = await Client.PostAsJsonAsync(
            "/api/v1/tenant-invitations/accept",
            new InvitationTokenBody(token));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("tenants.invitation.identity-conflict", body);
        Assert.DoesNotContain(existing.Email, body);
        Assert.DoesNotContain("existing-recipient", body);
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == Data.TenantId).SingleAsync();
        Assert.DoesNotContain(tenant.Members, item => item.UserId == "recipient");
        Assert.Equal(
            TenantInvitationStatus.Pending,
            (await Fixture.Database
                .GetCollection<TenantInvitation>("TenantInvitations")
                .Find(item => item.TenantId == tenant.Id)
                .SingleAsync()).Status);
        Assert.Equal(
            0,
            await Fixture.Database
                .GetCollection<TenantInvitationAcceptance>("TenantInvitationAcceptances")
                .CountDocumentsAsync(FilterDefinition<TenantInvitationAcceptance>.Empty));
        Assert.Equal(
            "",
            (await Fixture.DbContext.Users.Find(item => item.Id == existing.Id).SingleAsync()).DefaultTenantId);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("resend")]
    [InlineData("update")]
    public async Task TenantAdminCannotManageAnExistingOwnerInvitation(string operation)
    {
        using var created = await Client.PostAsJsonAsync(
            $"/api/v1/tenants/{Data.TenantId}/invitations",
            new CreateTenantInvitationBody("owner-invited@example.test", TenantRole.Owner));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var invitation = (await created.Content.ReadFromJsonAsync<InvitationDto>())!;
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == Data.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(
            item => item.Id == tenant.Id,
            tenant with
            {
                Members = tenant.Members.Select(member => member with { Role = TenantRole.Admin }).ToImmutableList()
            });
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var path = $"/api/v1/tenants/{tenant.Id}/invitations/{invitation.Id}";
        using var response = operation == "update"
            ? await Client.PutAsJsonAsync(path, new UpdateTenantInvitationBody(TenantRole.Member))
            : await Client.PostAsync(path + "/" + operation, null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<string> CreateInvitation()
    {
        using var result = await Client.PostAsJsonAsync(
            $"/api/v1/tenants/{Data.TenantId}/invitations",
            new CreateTenantInvitationBody("invited@example.test", TenantRole.Member));
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        Assert.DoesNotContain("TokenHash", await result.Content.ReadAsStringAsync());
        var invitation = await Fixture.Database
            .GetCollection<TenantInvitation>("TenantInvitations")
            .Find(item => item.TenantId == Data.TenantId)
            .SingleAsync();
        var token = await ApiServices.GetRequiredService<IEnvelopeEncryptionService>()
            .DecryptStringAsync(
                invitation.DeliveryToken,
                EnvelopeEncryptionContext.ForTenant(
                    invitation.TenantId,
                    "TenantInvitation",
                    invitation.Id,
                    "delivery-token",
                    invitation.Revision));
        return Assert.IsType<string>(token.Value);
    }
}
