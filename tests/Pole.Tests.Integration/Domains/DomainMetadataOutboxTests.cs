using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Persistence.DomainZones;
using Pole.Features.Domains.Application.Persistence.HostnameBindings;
using Pole.Features.Domains.Application.Realtime;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.Domains.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Realtime.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Domains;

public sealed class DomainMetadataOutboxTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private const string EventName = "pole.domains/v1/domain-metadata-changed";

    [Fact]
    public async Task CallerCommitAbortAndOutboxFailureFenceReservationHints()
    {
        var tenant = ObjectId.GenerateNewId().ToString();
        var target = ObjectId.GenerateNewId().ToString();
        var create = Creator();
        var runner = Services.GetRequiredService<MongoRunTransaction>();
        var aborted = await runner.InvokeOutcomeAsync<CreateManagedDomainZoneOutcome>(async (tx, ct) =>
        {
            var result = await create.InvokeAsync(tenant, target, "aborted", "example.test", 1, tx.Session, ct);
            Assert.IsType<TenantDomainZoneSnapshot>(result.Value);
            Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(EventName));
            return tx.Abort(result);
        });
        Assert.IsType<TenantDomainZoneSnapshot>(aborted.Value);
        Assert.Equal(0, await Fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones").CountDocumentsAsync(_ => true));
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(EventName));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.InvokeOutcomeAsync<CreateManagedDomainZoneOutcome>(async (tx, ct) =>
            tx.Commit(await Creator(fail: true).InvokeAsync(tenant, target, "failed", "example.test", 1, tx.Session, ct))));
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(EventName));
        Assert.Equal(0, await Fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones").CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task BindingCleanupEmitsOnlyAcceptedIdsAndRetainsCallerTransaction()
    {
        var tenant = ObjectId.GenerateNewId().ToString();
        var zone = TenantDomainZoneFactory.CreateManagedReservation(tenant, ObjectId.GenerateNewId().ToString(), "public", Pole.Dns.DnsName.Create("example.test"), new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
        await Fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones").InsertOneAsync(zone);
        var request = new CreateCustomHostnameBindingRequest(tenant, zone.Id, zone.DeploymentTargetId,
            ResourceTypeId.Create("pole/web-service"), ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), "binding");
        var cleanup = new MongoHostnameBindingCleanup(Fixture.Database, References, TimeProvider.System, Fixture.StoreOutboxWrites);
        var create = new MongoCreateCustomHostnameBinding(Fixture.Database, cleanup, TimeProvider.System, Fixture.StoreOutboxWrites);
        var delete = new MongoDeleteHostnameBindingsForResource(cleanup);
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            Assert.IsType<HostnameBindingSnapshot>((await create.InvokeAsync(request, session)).Value);
            Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(EventName));
            await session.CommitTransactionAsync();
        }
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            Assert.IsType<HostnameBindingsCleanedUp>((await delete.InvokeAsync(tenant, request.ResourceType, request.ResourceId, session)).Value);
            await session.AbortTransactionAsync();
        }
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(EventName));
        Assert.Equal(1, await Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings").CountDocumentsAsync(_ => true));
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            await delete.InvokeAsync(tenant, request.ResourceType, request.ResourceId, session);
            await delete.InvokeAsync(tenant, request.ResourceType, request.ResourceId, session);
            await session.CommitTransactionAsync();
        }
        Assert.Equal(2, await DbContext.CountOutboxMessagesAsync(EventName));
    }

    [Fact]
    public async Task DomainConsumerIsTenantWideAndTopicRegistersOnce()
    {
        RealtimeChange? notice = null;
        await new NotifyDomainMetadataChangedConsumer((change, _) => { notice = change; return Task.CompletedTask; })
            .HandleAsync(new("zone", 2, "binding") { EventCore = EventCore.Create("tenant") });
        Assert.Equal(new RealtimeChange("domains", "tenant"), notice);
        var services = new ServiceCollection().AddDomainsFeature();
        Assert.Equal(new RealtimeTopic("domains", RealtimeScope.Tenant), Assert.Single(services, item => item.ServiceType == typeof(RealtimeTopic)).ImplementationInstance);
    }

    private MongoCreateManagedDomainZoneReservation Creator(bool fail = false) => new(Fixture.Database, References,
        (_, _, _) => Task.FromResult<FenceDeploymentTargetWorkloadConfigurationsOutcome>(new DeploymentTargetWorkloadConfigurationsFenced()),
        TimeProvider.System, async (session, writes, ct) =>
        {
            await Fixture.StoreOutboxWrites(session, writes, ct);
            if (fail) throw new InvalidOperationException("Injected outbox failure.");
        });

    private static Task<ApplyDeploymentTargetReferenceChangesOutcome> References(DeploymentTargetReferenceOwner owner,
        IReadOnlyCollection<string> added, IReadOnlyCollection<string> removed, IClientSessionHandle session, CancellationToken ct = default) =>
        Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(new DeploymentTargetReferencesChanged());
}
