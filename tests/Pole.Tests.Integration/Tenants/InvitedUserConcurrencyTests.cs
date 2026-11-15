using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Endpoints.Invitations;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Domain;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

[Collection(IntegrationTestCollection.Global3)]
public sealed class InvitedUserConcurrencyTests(
    MongoReplicaSetFixture fixture
) : IsolatedApiIntegrationTestBase(fixture)
{
    private readonly JoinRaceClock _clock = new();

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<TimeProvider>(_clock);

    [Fact]
    public async Task EmailClaimedAfterSnapshotReadReturnsConflictAndRollsBackAcceptance()
    {
        using var created = await Client.PostAsJsonAsync(
            $"/api/v1/tenants/{Data.TenantId}/invitations",
            new CreateTenantInvitationBody("racing@example.test", TenantRole.Member));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var invitations = Fixture.Database.GetCollection<TenantInvitation>("TenantInvitations");
        var invitation = await invitations.Find(item => item.TenantId == Data.TenantId).SingleAsync();
        var token = await ApiServices.GetRequiredService<IEnvelopeEncryptionService>()
            .DecryptStringAsync(
                invitation.DeliveryToken,
                EnvelopeEncryptionContext.ForTenant(
                    invitation.TenantId,
                    "TenantInvitation",
                    invitation.Id,
                    "delivery-token",
                    invitation.Revision));
        Client.DefaultRequestHeaders.Remove(TestAuthenticationHandler.UserHeader);
        Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, "racing-recipient");
        Client.DefaultRequestHeaders.Remove(TestAuthenticationHandler.EmailHeader);
        Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.EmailHeader, "racing@example.test");
        Client.DefaultRequestHeaders.Add("X-Test-Email-Verified", "true");
        var beforeOutbox = await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(_ => true);
        // Acceptance reads its clock once. Users reads it again after both identity reads,
        // while constructing the upsert. Commit a competing user in that exact gap.
        _clock.BeforeUserWrite = () => Fixture.DbContext.Users.InsertOne(
            PoleUserFactory.Create("competing-subject", "racing@example.test", null, null, "", DateTime.UtcNow));
        using var response = await Client.PostAsJsonAsync(
            "/api/v1/tenant-invitations/accept",
            new InvitationTokenBody(Assert.IsType<string>(token.Value)));
        Assert.True(_clock.Triggered);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("tenants.invitation.identity-conflict", problem);
        Assert.DoesNotContain("competing-subject", problem);
        Assert.False(await Fixture.DbContext.Users.Find(item => item.Id == "racing-recipient").AnyAsync());
        Assert.Equal(
            "",
            (await Fixture.DbContext.Users.Find(item => item.Id == "competing-subject").SingleAsync()).DefaultTenantId);
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == Data.TenantId).SingleAsync();
        Assert.DoesNotContain(tenant.Members, item => item.UserId == "racing-recipient");
        Assert.Equal(
            TenantInvitationStatus.Pending,
            (await invitations.Find(item => item.Id == invitation.Id).SingleAsync()).Status);
        Assert.Equal(
            0,
            await Fixture.Database
                .GetCollection<TenantInvitationAcceptance>("TenantInvitationAcceptances")
                .CountDocumentsAsync(_ => true));
        Assert.Equal(beforeOutbox, await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(_ => true));
    }

    private sealed class JoinRaceClock : TimeProvider
    {
        private int _reads;
        public Action? BeforeUserWrite { get; set; }
        public bool Triggered { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            if (BeforeUserWrite is not null && Interlocked.Increment(ref _reads) == 2)
            {
                Triggered = true;
                BeforeUserWrite();
            }

            return DateTimeOffset.UtcNow;
        }
    }
}
