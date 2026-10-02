using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Features.PrivateAccess.Application;
using Pole.Features.PrivateAccess.Application.Persistence;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.PrivateAccess;

[Collection(IntegrationTestCollection.Name)]
public sealed class GrantPersistenceTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    private const string TenantId = "66a000000000000000000001";
    private const string OtherTenantId = "66a000000000000000000002";
    private const string ResourceId = "66a000000000000000000003";
    private const string DeploymentId = "66a000000000000000000004";
    private const string GroupId = "66a000000000000000000005";
    private const string ActorId = "actor";
    private const string SubjectId = "subject";
    private static readonly ResourceTypeId ResourceType = ResourceTypeId.Create("pole/redis");
    private ServiceProvider provider = null!;
    private bool targetAttested = true;
    private long deploymentRevision = 1;

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        await fixture.DbContext.Tenants.InsertManyAsync([
            TenantFactory.Create("One", TenantId) with
            {
                Members = ImmutableList.Create(
                    TenantFactory.CreateMember(TenantId, ActorId, "actor@example.com", TenantRole.Admin),
                    TenantFactory.CreateMember(TenantId, SubjectId, "subject@example.com", TenantRole.Viewer))
            },
            TenantFactory.Create("Two", OtherTenantId) with
            {
                Members = ImmutableList.Create(
                    TenantFactory.CreateMember(OtherTenantId, ActorId, "actor@example.com", TenantRole.Admin),
                    TenantFactory.CreateMember(OtherTenantId, SubjectId, "subject@example.com", TenantRole.Viewer))
            }
        ]);

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(fixture.Client);
        services.AddSingleton(fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton<StoreOutboxWrites>(fixture.StoreOutboxWrites);
        services.AddTenantsFeature();
        services.AddPrivateAccessFeature();
        services.AddSingleton<FenceRegisteredResource>(
            (_, _, _, session, _) =>
            {
                Assert.True(session.IsInTransaction);
                return Task.FromResult<RegisteredResource?>(new RegisteredResource(
                    "registration", TenantId, GroupId, ResourceType, ResourceId,
                    "Redis", null, DateTime.UtcNow, DateTime.UtcNow));
            });
        services.AddSingleton<LoadExactResourceDeploymentAccessPlacement>(
            (tenantId, _, _, _, session, _) =>
            {
                Assert.True(session.IsInTransaction);
                return Task.FromResult<ResourceDeploymentAccessPlacement?>(
                    tenantId == TenantId ? new ResourceDeploymentAccessPlacement(
                        TenantId, ResourceType.Value, ResourceId, GroupId, DeploymentId,
                        "target", "cell", "scope", "namespace", deploymentRevision, 1,
                        "Applied", "Apply") : null);
            });
        services.AddSingleton<LoadExactResourceDeploymentRuntimePlacement>(
            (tenantId, _, resourceId, deploymentId, _) =>
                Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(
                    tenantId == TenantId && resourceId == ResourceId && deploymentId == DeploymentId
                        ? new ResourceDeploymentRuntimePlacementDto(
                            DeploymentId, ResourceId, "cell", "target", "namespace",
                            deploymentRevision, 1, null!, "Applied", "Apply", "scope")
                        : null));
        services.AddSingleton<FenceDeploymentTargetPrivateAccess>(
            (targetId, session, _) =>
            {
                Assert.True(session.IsInTransaction);
                return Task.FromResult<FenceDeploymentTargetPrivateAccessOutcome>(
                    targetAttested
                        ? new PrivateAccessDeploymentTargetReady(targetId, 1)
                        : new PrivateAccessDeploymentTargetUnavailable());
            });
        services.AddSingleton<ApplyDeploymentTargetReferenceChanges>(
            (_, _, _, session, _) =>
            {
                Assert.True(session.IsInTransaction);
                return Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
                    new DeploymentTargetReferencesChanged());
            });
        services.AddSingleton<IResourceConnectionDestinationCapability>(new EndpointCapability());
        provider = services.BuildServiceProvider();
    }

    public Task DisposeAsync()
    {
        provider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_WritesGrantAuditAndOutboxAtomically()
    {
        var outcome = await provider.GetRequiredService<PersistPrivateAccessGrant>()(Command());
        var grant = Assert.IsType<PrivateAccessGrant>(outcome.Value);
        Assert.Equal(TenantId, grant.TenantId);
        Assert.IsType<ActivePrivateAccessGrant>(grant.State);
        Assert.Single(await fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .Find(Builders<PrivateAccessGrant>.Filter.Empty).ToListAsync());
        var audit = Assert.Single(await fixture.Database.GetCollection<PrivateAccessAudit>("PrivateAccessAudit")
            .Find(Builders<PrivateAccessAudit>.Filter.Empty).ToListAsync());
        Assert.Equal(grant.Id, audit.GrantId);
        Assert.IsType<PrivateAccessGrantCreatedAudit>(audit.State);
        var storedAudit = await fixture.Database.GetCollection<BsonDocument>("PrivateAccessAudit")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(audit.Id)))
            .SingleAsync();
        Assert.True(storedAudit.Contains("State"));
        Assert.False(storedAudit.Contains("Action"));
        Assert.False(storedAudit.Contains("Reason"));
        Assert.Equal(1, await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
            Builders<Pole.Features.Outbox.Application.Model.OutboxMessage>.Filter.Empty));
        var roundTrip = BsonSerializer.Deserialize<PrivateAccessGrant>(grant.ToBson());
        Assert.IsType<ActivePrivateAccessGrant>(roundTrip.State);
        Assert.IsType<RevokedPrivateAccessGrant>(
            BsonSerializer.Deserialize<PrivateAccessGrant>(
                grant.Revoke(ActorId, DateTime.UtcNow).ToBson()).State);
    }

    [Fact]
    public async Task Create_RejectsActorWithoutAdminRoleAndMissingSubject_WithoutWrites()
    {
        var viewer = await provider.GetRequiredService<PersistPrivateAccessGrant>()(
            Command() with { ActorUserId = SubjectId });
        Assert.IsType<PrivateAccessManageRoleRequired>(viewer.Value);
        var absent = await provider.GetRequiredService<PersistPrivateAccessGrant>()(
            Command() with { SubjectUserId = "absent" });
        Assert.IsType<PrivateAccessSubjectNotMember>(absent.Value);
        Assert.Equal(0, await fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .CountDocumentsAsync(Builders<PrivateAccessGrant>.Filter.Empty));
        Assert.Equal(0, await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
            Builders<Pole.Features.Outbox.Application.Model.OutboxMessage>.Filter.Empty));
    }

    [Fact]
    public async Task Create_RejectsCrossTenantDeployment_WithoutWrites()
    {
        var outcome = await provider.GetRequiredService<PersistPrivateAccessGrant>()(
            Command() with { TenantId = OtherTenantId });
        Assert.IsType<PrivateAccessResourceUnavailable>(outcome.Value);
        Assert.Equal(0, await fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .CountDocumentsAsync(Builders<PrivateAccessGrant>.Filter.Empty));
    }

    [Fact]
    public async Task SessionTicket_IsStoredHashedAndRedeemsOnlyOnce()
    {
        var grant = await CreateGrantAsync();
        var issued = Assert.IsType<PrivateAccessSessionIssued>(
            (await provider.GetRequiredService<IssuePrivateAccessSession>()(
                TenantId, grant.Id, SubjectId)).Value);
        var stored = await fixture.Database.GetCollection<PrivateAccessSession>("PrivateAccessSessions")
            .Find(item => item.Id == issued.SessionId).SingleAsync();
        Assert.NotEqual(issued.Ticket, stored.Ticket.Hash);
        Assert.IsType<PrivateAccessTicketPending>(stored.State);
        Assert.True(issued.ExpiresAtUtc <= DateTime.UtcNow.AddSeconds(30));

        var redeem = provider.GetRequiredService<RedeemPrivateAccessSession>();
        var lease = Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await redeem(issued.Ticket)).Value);
        Assert.Equal(grant.Id, lease.GrantId);
        Assert.Equal("target", lease.DeploymentTargetId);
        Assert.Equal("redis-service", lease.ServiceName);
        Assert.Equal(6379, lease.ServicePort.Value);
        Assert.True(lease.ExpiresAtUtc <= DateTime.UtcNow.AddSeconds(10));
        Assert.IsType<PrivateAccessTicketUsed>((await redeem(issued.Ticket)).Value);

        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);
        var started = await provider.GetRequiredService<MarkPrivateAccessSessionStarted>()(
            issued.SessionId);
        Assert.IsType<PrivateAccessSessionStarted>(started.Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);
        Assert.IsType<PrivateAccessSessionClosed>(
            (await provider.GetRequiredService<ClosePrivateAccessSession>()(
                issued.SessionId, PrivateAccessSessionCloseReason.ClientDisconnected)).Value);
        Assert.IsType<PrivateAccessSessionUnavailable>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);
        var audit = await fixture.Database.GetCollection<PrivateAccessAudit>("PrivateAccessAudit")
            .Find(item => item.SessionId == issued.SessionId).ToListAsync();
        Assert.Contains(audit, item => item.State is PrivateAccessSessionStartedAudit);
        Assert.Contains(audit, item => item.State is PrivateAccessSessionClosedAudit);
        Assert.All(audit, item => Assert.DoesNotContain(issued.Ticket, item.ToBson().ToJson()));
    }

    [Fact]
    public async Task NonCanonicalTicketCannotRedeemAnOtherwiseMatchingSession()
    {
        var grant = await CreateGrantAsync();
        var issued = Assert.IsType<PrivateAccessSessionIssued>(
            (await provider.GetRequiredService<IssuePrivateAccessSession>()(
                TenantId, grant.Id, SubjectId)).Value);
        var malformedTicket = "pole_pa_" + new string('g', 64);
        var malformedHash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(malformedTicket)));
        var sessions = fixture.Database.GetCollection<PrivateAccessSession>("PrivateAccessSessions");
        await sessions.UpdateOneAsync(
            item => item.Id == issued.SessionId,
            Builders<PrivateAccessSession>.Update.Set(item => item.Ticket.Hash, malformedHash));

        var outcome = await provider.GetRequiredService<RedeemPrivateAccessSession>()(malformedTicket);

        Assert.IsType<PrivateAccessTicketInvalid>(outcome.Value);
        var stored = await sessions.Find(item => item.Id == issued.SessionId).SingleAsync();
        Assert.IsType<PrivateAccessTicketPending>(stored.State);
    }

    [Fact]
    public async Task RedeemedSession_CannotReviveAfterAuthorizationExpires()
    {
        var grant = await CreateGrantAsync();
        var issued = Assert.IsType<PrivateAccessSessionIssued>(
            (await provider.GetRequiredService<IssuePrivateAccessSession>()(
                TenantId, grant.Id, SubjectId)).Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RedeemPrivateAccessSession>()(
                issued.Ticket)).Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);

        var collection = fixture.Database.GetCollection<PrivateAccessSession>(
            "PrivateAccessSessions");
        var current = await collection.Find(item => item.Id == issued.SessionId).SingleAsync();
        var redeemed = Assert.IsType<PrivateAccessTicketRedeemed>(current.State);
        await collection.ReplaceOneAsync(item => item.Id == issued.SessionId,
            current with
            {
                State = redeemed with
                {
                    LastAuthorizationExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1)
                },
                Revision = current.Revision + 1
            });
        Assert.IsType<PrivateAccessSessionUnavailable>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);
        Assert.IsType<PrivateAccessSessionUnavailable>(
            (await provider.GetRequiredService<MarkPrivateAccessSessionStarted>()(
                issued.SessionId)).Value);
    }

    [Fact]
    public async Task RedeemedSession_AllowsFirstChallengeDuringThirtySecondSetup()
    {
        var grant = await CreateGrantAsync();
        var issued = Assert.IsType<PrivateAccessSessionIssued>(
            (await provider.GetRequiredService<IssuePrivateAccessSession>()(
                TenantId, grant.Id, SubjectId)).Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RedeemPrivateAccessSession>()(
                issued.Ticket)).Value);
        var collection = fixture.Database.GetCollection<PrivateAccessSession>(
            "PrivateAccessSessions");
        var current = await collection.Find(item => item.Id == issued.SessionId).SingleAsync();
        var redeemed = Assert.IsType<PrivateAccessTicketRedeemed>(current.State);
        Assert.Null(redeemed.LastAuthorizationExpiresAtUtc);
        await collection.ReplaceOneAsync(item => item.Id == issued.SessionId,
            current with
            {
                State = redeemed with { RedeemedAtUtc = DateTime.UtcNow.AddSeconds(-20) },
                Revision = current.Revision + 1
            });
        Assert.IsType<PrivateAccessSessionUnavailable>(
            (await provider.GetRequiredService<MarkPrivateAccessSessionStarted>()(
                issued.SessionId)).Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);
        Assert.IsType<PrivateAccessSessionStarted>(
            (await provider.GetRequiredService<MarkPrivateAccessSessionStarted>()(
                issued.SessionId)).Value);
    }

    [Fact]
    public async Task Renewal_ClosesWhenDeploymentBindingOrTargetAttestationChanges()
    {
        var grant = await CreateGrantAsync();
        var issued = Assert.IsType<PrivateAccessSessionIssued>(
            (await provider.GetRequiredService<IssuePrivateAccessSession>()(
                TenantId, grant.Id, SubjectId)).Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RedeemPrivateAccessSession>()(
                issued.Ticket)).Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);
        Assert.IsType<PrivateAccessSessionStarted>(
            (await provider.GetRequiredService<MarkPrivateAccessSessionStarted>()(
                issued.SessionId)).Value);

        deploymentRevision = 2;
        Assert.IsType<PrivateAccessBindingChanged>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);
        Assert.IsType<PrivateAccessSessionUnavailable>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);

        deploymentRevision = 1;
        var second = Assert.IsType<PrivateAccessSessionIssued>(
            (await provider.GetRequiredService<IssuePrivateAccessSession>()(
                TenantId, grant.Id, SubjectId)).Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RedeemPrivateAccessSession>()(
                second.Ticket)).Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                second.SessionId)).Value);
        Assert.IsType<PrivateAccessSessionStarted>(
            (await provider.GetRequiredService<MarkPrivateAccessSessionStarted>()(
                second.SessionId)).Value);
        targetAttested = false;
        Assert.IsType<PrivateAccessDeploymentUnavailable>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                second.SessionId)).Value);
    }

    [Fact]
    public async Task Cleanup_ClaimsBeforeReturningAndAcknowledgesAfterSessionClosure()
    {
        var grant = await CreateGrantAsync();
        var issued = Assert.IsType<PrivateAccessSessionIssued>(
            (await provider.GetRequiredService<IssuePrivateAccessSession>()(
                TenantId, grant.Id, SubjectId)).Value);
        var loader = provider.GetRequiredService<LoadExpiredPrivateAccessSessionCleanupPage>();
        var expired = issued.ExpiresAtUtc.AddMinutes(1);
        var claimed = Assert.Single(await loader(expired, 10));
        Assert.Equal(issued.SessionId, claimed.SessionId);
        Assert.Equal("target", claimed.DeploymentTargetId);
        Assert.IsType<PrivateAccessSessionCleaning>((await fixture.Database
            .GetCollection<PrivateAccessSession>("PrivateAccessSessions")
            .Find(item => item.Id == issued.SessionId).SingleAsync()).State);
        Assert.IsType<PrivateAccessTicketUsed>(
            (await provider.GetRequiredService<RedeemPrivateAccessSession>()(
                issued.Ticket)).Value);
        Assert.Equal(issued.SessionId, Assert.Single(await loader(expired, 10)).SessionId);
        Assert.IsType<Pole.Features.PrivateAccess.Contracts.PrivateAccessSessionCleaned>(
            (await provider.GetRequiredService<MarkPrivateAccessSessionCleaned>()(
                issued.SessionId)).Value);
        Assert.Empty(await loader(expired, 10));
    }

    [Fact]
    public async Task GatewayDesiredState_TracksOnlyActiveGrantEndpoints()
    {
        var grant = await CreateGrantAsync();
        var load = provider.GetRequiredService<LoadPrivateAccessGatewayDesiredStates>();
        var desired = Assert.Single(await load());
        Assert.Equal((TenantId, "target"), (desired.TenantId, desired.DeploymentTargetId));
        var endpoint = Assert.Single(desired.Endpoints);
        Assert.Equal((DeploymentId, "redis", "namespace", "redis-service", 6379),
            (endpoint.ResourceDeploymentId, endpoint.EndpointId,
                endpoint.KubernetesNamespace, endpoint.ServiceName, endpoint.TargetPort));

        var grants = fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants");
        await grants.ReplaceOneAsync(item => item.Id == grant.Id,
            grant.Revoke(ActorId, DateTime.UtcNow));
        Assert.Empty(await load());
    }

    [Fact]
    public async Task GatewayRegistration_FencesRotationAndRevocationByGeneration()
    {
        const string targetId = "66a000000000000000000006";
        var gatewayId = PrivateAccessGatewayIds.StableId(TenantId, targetId);
        var identity = new PrivateAccessGatewayIdentity(TenantId, targetId, gatewayId, 1, 1);
        var endpoint = new PrivateAccessGatewayEndpointSnapshot(
            "redis", "cell", DeploymentId, "namespace", "redis-service", 6379, 6379);
        var expiry = DateTime.UnixEpoch.AddDays(30000);
        var save = provider.GetRequiredService<SavePrivateAccessGatewayRegistration>();
        var load = provider.GetRequiredService<LoadPrivateAccessGatewayRegistrations>();
        var resolve = provider.GetRequiredService<ResolvePrivateAccessGatewayIdentity>();
        var remove = provider.GetRequiredService<RemovePrivateAccessGatewayRegistration>();
        var oldPin = new string('a', 64);
        var newPin = new string('b', 64);

        Assert.True(await save(identity, oldPin, expiry, [endpoint]));
        Assert.True(await save(identity, oldPin, expiry, [endpoint]));
        Assert.Equal(identity, await resolve(oldPin));
        Assert.Equal(endpoint, Assert.Single(Assert.Single(await load()).Endpoints));

        var rotated = identity with { Generation = 2, Revision = 2 };
        Assert.True(await save(rotated, newPin, expiry, [endpoint]));
        Assert.Null(await resolve(oldPin));
        Assert.Equal(rotated, await resolve(newPin));
        Assert.False(await remove(identity));
        Assert.True(await remove(rotated));
        Assert.Empty(await load());
    }

    [Fact]
    public async Task BrokerOwnership_FencesConcurrentClaimsExpiryAndGatewayRotation()
    {
        const string targetId = "66a000000000000000000006";
        var gatewayId = PrivateAccessGatewayIds.StableId(TenantId, targetId);
        var gateway = new PrivateAccessGatewayIdentity(TenantId, targetId, gatewayId, 1, 1);
        var endpoint = new PrivateAccessGatewayEndpointSnapshot(
            "redis", "cell", DeploymentId, "namespace", "redis-service", 6379, 6379);
        var save = provider.GetRequiredService<SavePrivateAccessGatewayRegistration>();
        var claim = provider.GetRequiredService<ClaimPrivateAccessBrokerOwner>();
        var renew = provider.GetRequiredService<RenewPrivateAccessBrokerOwner>();
        var release = provider.GetRequiredService<ReleasePrivateAccessBrokerOwner>();
        var load = provider.GetRequiredService<LoadPrivateAccessBrokerOwner>();
        var remove = provider.GetRequiredService<RemovePrivateAccessGatewayRegistration>();
        var pin = new string('a', 64);
        var expiry = DateTime.UnixEpoch.AddDays(30000);
        Assert.True(await save(gateway, pin, expiry, [endpoint]));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            claim(gateway, $"replica-{index}", $"https://replica-{index}.internal:9445")));
        var first = Assert.Single(attempts.Select(result => result.Value)
            .OfType<PrivateAccessBrokerOwnerLease>());
        Assert.Equal(7, attempts.Count(result => result.Value is PrivateAccessBrokerOwnerBusy));
        var loaded = Assert.IsType<PrivateAccessBrokerOwnerLease>(
            await load(TenantId, targetId));
        Assert.Equal(first.Gateway, loaded.Gateway);
        Assert.Equal(first.InstanceId, loaded.InstanceId);
        Assert.Equal(first.Epoch, loaded.Epoch);
        Assert.InRange(Math.Abs((loaded.ExpiresAtUtc - first.ExpiresAtUtc).TotalMilliseconds),
            0, 1);
        Assert.True(await renew(first));
        Assert.IsType<PrivateAccessBrokerOwnerBusy>((await claim(gateway,
            "other", "https://other.internal:9445")).Value);

        var owners = fixture.Database.GetCollection<PrivateAccessBrokerOwner>(
            "PrivateAccessBrokerOwners");
        await owners.UpdateOneAsync(item => item.Id == gatewayId,
            Builders<PrivateAccessBrokerOwner>.Update.Set(
                item => item.LeaseExpiresAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        Assert.False(await renew(first));
        var recovered = Assert.IsType<PrivateAccessBrokerOwnerLease>((await claim(
            gateway, "replacement", "https://replacement.internal:9445")).Value);
        Assert.True(recovered.Epoch > first.Epoch);
        await release(first);
        Assert.Equal(recovered.Epoch, (await load(TenantId, targetId))?.Epoch);

        var rotated = gateway with { Generation = 2, Revision = 2 };
        Assert.True(await save(rotated, new string('b', 64), expiry, [endpoint]));
        Assert.False(await renew(recovered));
        Assert.Null(await load(TenantId, targetId));
        Assert.IsType<PrivateAccessBrokerGatewayUnavailable>((await claim(
            gateway, "stale", "https://stale.internal:9445")).Value);
        var current = Assert.IsType<PrivateAccessBrokerOwnerLease>((await claim(
            rotated, "replacement", "https://replacement.internal:9445")).Value);
        Assert.True(current.Epoch > recovered.Epoch);
        Assert.True(await remove(rotated));
        Assert.Null(await load(TenantId, targetId));
        Assert.False(await renew(current));
        Assert.True(await save(gateway, new string('c', 64), expiry, [endpoint]));
        var recreated = Assert.IsType<PrivateAccessBrokerOwnerLease>((await claim(
            gateway, "replacement", "https://replacement.internal:9445")).Value);
        Assert.True(recreated.Epoch > current.Epoch);
        await release(current);
        Assert.Equal(recreated.Epoch, (await load(TenantId, targetId))?.Epoch);
    }

    [Fact]
    public async Task Cleanup_UnacknowledgedPageRotatesToNextEligibleSession()
    {
        var grant = await CreateGrantAsync();
        var issue = provider.GetRequiredService<IssuePrivateAccessSession>();
        var firstSession = Assert.IsType<PrivateAccessSessionIssued>(
            (await issue(TenantId, grant.Id, SubjectId)).Value);
        var secondSession = Assert.IsType<PrivateAccessSessionIssued>(
            (await issue(TenantId, grant.Id, SubjectId)).Value);
        var loader = provider.GetRequiredService<LoadExpiredPrivateAccessSessionCleanupPage>();
        var now = firstSession.ExpiresAtUtc.AddMinutes(1);

        var firstPage = Assert.Single(await loader(now, 1));
        var secondPage = Assert.Single(await loader(now, 1));
        Assert.NotEqual(firstPage.SessionId, secondPage.SessionId);
        Assert.Equal(
            new[] { firstSession.SessionId, secondSession.SessionId }.Order(),
            new[] { firstPage.SessionId, secondPage.SessionId }.Order());

        var thirdPage = Assert.Single(await loader(now, 1));
        Assert.Equal(firstPage.SessionId, thirdPage.SessionId);
        var claimed = await fixture.Database.GetCollection<PrivateAccessSession>(
            "PrivateAccessSessions").Find(item => item.Id == firstPage.SessionId)
            .SingleAsync();
        Assert.IsType<PrivateAccessSessionCleaning>(claimed.State);
        Assert.True(claimed.CleanupLastAttemptAtUtc > now);
    }

    [Fact]
    public async Task SessionIssueDenial_WritesMetadataAuditWithoutSessionOrTicket()
    {
        var grant = await CreateGrantAsync();
        var denied = await provider.GetRequiredService<IssuePrivateAccessSession>()(
            TenantId, grant.Id, ActorId);
        Assert.IsType<PrivateAccessSessionSubjectMismatch>(denied.Value);
        Assert.Equal(0, await fixture.Database.GetCollection<PrivateAccessSession>(
            "PrivateAccessSessions").CountDocumentsAsync(
            Builders<PrivateAccessSession>.Filter.Empty));
        var audit = await fixture.Database.GetCollection<PrivateAccessAudit>("PrivateAccessAudit")
            .Find(item => item.GrantId == grant.Id).ToListAsync();
        Assert.Contains(audit, item =>
            item.State is PrivateAccessSessionIssueDeniedAudit
            { Reason: PrivateAccessAuditReason.SubjectMismatch });
        Assert.All(audit, item => Assert.Null(item.SessionId));
        Assert.Equal(1, await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
            Builders<Pole.Features.Outbox.Application.Model.OutboxMessage>.Filter.Empty));
    }

    [Fact]
    public async Task Cleanup_DoesNotClaimFreshActiveLease_AndExpiredLeaseCannotRevive()
    {
        var grant = await CreateGrantAsync();
        var issued = Assert.IsType<PrivateAccessSessionIssued>(
            (await provider.GetRequiredService<IssuePrivateAccessSession>()(
                TenantId, grant.Id, SubjectId)).Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RedeemPrivateAccessSession>()(
                issued.Ticket)).Value);
        Assert.IsType<PrivateAccessLeaseSnapshot>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);
        Assert.IsType<PrivateAccessSessionStarted>(
            (await provider.GetRequiredService<MarkPrivateAccessSessionStarted>()(
                issued.SessionId)).Value);
        var loader = provider.GetRequiredService<LoadExpiredPrivateAccessSessionCleanupPage>();
        Assert.Empty(await loader(DateTime.UtcNow, 10));

        var collection = fixture.Database.GetCollection<PrivateAccessSession>(
            "PrivateAccessSessions");
        var current = await collection.Find(item => item.Id == issued.SessionId).SingleAsync();
        var active = Assert.IsType<PrivateAccessSessionActive>(current.State);
        await collection.ReplaceOneAsync(item => item.Id == issued.SessionId,
            current with
            {
                State = active with { LastLeaseExpiresAtUtc = DateTime.UtcNow.AddSeconds(-31) },
                Revision = current.Revision + 1
            });
        Assert.IsType<PrivateAccessSessionUnavailable>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);
        Assert.Equal(issued.SessionId,
            Assert.Single(await loader(DateTime.UtcNow, 10)).SessionId);
        Assert.IsType<PrivateAccessSessionUnavailable>(
            (await provider.GetRequiredService<RenewPrivateAccessSession>()(
                issued.SessionId)).Value);
    }

    private async Task<PrivateAccessGrant> CreateGrantAsync() =>
        Assert.IsType<PrivateAccessGrant>(
            (await provider.GetRequiredService<PersistPrivateAccessGrant>()(Command())).Value);

    private static CreatePrivateAccessGrantCommand Command() => new(
        TenantId, ActorId, SubjectId, ResourceType, ResourceId,
        DeploymentId, "redis", DateTime.UtcNow.AddHours(1));

    private sealed class EndpointCapability : IResourceConnectionDestinationCapability
    {
        public ResourceTypeId ResourceType => GrantPersistenceTests.ResourceType;

        public Task<LoadResourceConnectionEndpointsOutcome> LoadAsync(
            string tenantId,
            string resourceId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            Assert.True(session.IsInTransaction);
            return Task.FromResult<LoadResourceConnectionEndpointsOutcome>(
                new ResourceConnectionEndpointsFound(ImmutableList.Create(
                    new ResourceConnectionEndpoint(
                        "redis", "redis-service", NetworkPort.Create(6379),
                        NetworkPort.Create(6379)))));
        }

        public Task<IReadOnlyDictionary<string, ResourceConnectionEndpointsFound>> LoadAvailableAsync(
            string tenantId,
            IReadOnlyCollection<string> resourceIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ResourceConnectionEndpointsFound>>(
                resourceIds.ToDictionary(id => id, id =>
                    new ResourceConnectionEndpointsFound(ImmutableList.Create(
                        new ResourceConnectionEndpoint("redis", "redis-service",
                            NetworkPort.Create(6379), NetworkPort.Create(6379))))));
    }
}
