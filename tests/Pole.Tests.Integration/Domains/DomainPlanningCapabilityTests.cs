using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.Domains.Application.Migrations;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Name)]
public sealed class DomainPlanningCapabilityTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        await new TenantDomainZoneSchemaValidationMigration().Up(fixture.Database);
        await new HostnameBindingSchemaValidationMigration().Up(fixture.Database);
        await new DomainIndexesMigration().Up(fixture.Database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Named_reads_are_tenant_scoped_and_generated_identity_is_not_named()
    {
        var zone = Zone();
        var binding = Binding(zone);
        await Zones.InsertOneAsync(zone);
        await Bindings.InsertOneAsync(binding);
        var provider = new MongoDomainPlanning(fixture.Database);

        Assert.IsType<NamedDomainZoneFound>((await provider.ResolveZoneAsync(
            new NamedDomainZoneRequest(zone.TenantId, "company-zone"), null, default)).Value);
        Assert.IsType<NamedHostnameBindingFound>((await provider.ResolveBindingAsync(
            new NamedHostnameBindingRequest(zone.TenantId, "api-alias"), null, default)).Value);
        Assert.IsType<NamedDomainZoneNotFound>((await provider.ResolveZoneAsync(
            new NamedDomainZoneRequest(Id(), "company-zone"), null, default)).Value);
        Assert.IsType<NamedHostnameBindingNotFound>((await provider.ResolveBindingAsync(
            new NamedHostnameBindingRequest(Id(), "api-alias"), null, default)).Value);
        Assert.Null(await provider.LoadZoneAsync(new LoadDomainZonePlanningRequest(Id(), zone.Id), null, default));
        Assert.Null(await provider.LoadBindingAsync(
            new LoadHostnameBindingPlanningRequest(Id(), binding.Id), null, default));

        var generated = TenantDomainZoneFactory.CreateGenerated(
            zone.TenantId, Id(), "pole-generated-system", DnsName.Create("example.test"),
            new TenantDomainZoneFactory.CreateOptions(Id(), DateTime.UtcNow));
        await Zones.InsertOneAsync(generated);
        Assert.IsType<NamedDomainZoneNotFound>((await provider.ResolveZoneAsync(
            new NamedDomainZoneRequest(zone.TenantId, generated.Label), null, default)).Value);
    }

    [Fact]
    public async Task Fences_are_real_conditional_writes_without_business_changes_and_rollback_with_caller()
    {
        var zone = Zone();
        var binding = Binding(zone);
        await Zones.InsertOneAsync(zone);
        await Bindings.InsertOneAsync(binding);
        var provider = new MongoDomainPlanning(fixture.Database);
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<DomainZoneFenced>((await provider.FenceZoneAsync(
            new FenceDomainZoneRequest(zone.TenantId, zone.Id, zone.Revision, 0), session, default)).Value);
        Assert.IsType<HostnameBindingFenced>((await provider.FenceBindingAsync(
            new FenceHostnameBindingRequest(zone.TenantId, binding.Id, 0), session, default)).Value);
        Assert.IsType<DomainZonePreconditionChanged>((await provider.FenceZoneAsync(
            new FenceDomainZoneRequest(zone.TenantId, zone.Id, zone.Revision, 0), session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(0, (await Zones.Find(item => item.Id == zone.Id).SingleAsync()).ReferenceFence);
        Assert.Equal(0, (await Bindings.Find(item => item.Id == binding.Id).SingleAsync()).ReferenceFence);

        session.StartTransaction();
        Assert.IsType<DomainZoneFenced>((await provider.FenceZoneAsync(
            new FenceDomainZoneRequest(zone.TenantId, zone.Id, zone.Revision, 0), session, default)).Value);
        Assert.IsType<HostnameBindingFenced>((await provider.FenceBindingAsync(
            new FenceHostnameBindingRequest(zone.TenantId, binding.Id, 0), session, default)).Value);
        await session.CommitTransactionAsync();
        var storedZone = await Zones.Find(item => item.Id == zone.Id).SingleAsync();
        Assert.Equal(zone.Revision, storedZone.Revision);
        Assert.Equal(zone.UpdatedAtUtc.Ticks / TimeSpan.TicksPerMillisecond,
            storedZone.UpdatedAtUtc.Ticks / TimeSpan.TicksPerMillisecond);
        Assert.Equal(1, storedZone.ReferenceFence);
        Assert.Equal(1, (await Bindings.Find(item => item.Id == binding.Id).SingleAsync()).ReferenceFence);
    }

    [Fact]
    public async Task Named_identity_is_unique_within_tenant_and_independent_from_dns_label()
    {
        var zone = Zone();
        await Zones.InsertOneAsync(zone);
        var duplicate = TenantDomainZoneFactory.CreateManagedReservation(
            zone.TenantId, Id(), "other", DnsName.Create("example.test"), "company-zone",
            new TenantDomainZoneFactory.CreateOptions(Id(), DateTime.UtcNow));
        var conflict = await Assert.ThrowsAsync<MongoWriteException>(() => Zones.InsertOneAsync(duplicate));
        Assert.True(Pole.Database.MongoDuplicateKey.IsForIndex(
            conflict, DomainIndexNames.ZoneTechnicalName, Zones.CollectionNamespace.CollectionName));
        await Zones.InsertOneAsync(duplicate with { TenantId = Id() });

        var binding = Binding(zone);
        await Bindings.InsertOneAsync(binding);
        var duplicateBinding = HostnameBindingFactory.CreateCustom(
            zone.TenantId, zone.Id, zone.DeploymentTargetId, ResourceTypeId.Create("pole/web-service"),
            Id(), Id(), "other", DnsName.Create(zone.Domain), "api-alias",
            new HostnameBindingFactory.CreateOptions(Id(), DateTime.UtcNow));
        var bindingConflict = await Assert.ThrowsAsync<MongoWriteException>(
            () => Bindings.InsertOneAsync(duplicateBinding));
        Assert.True(Pole.Database.MongoDuplicateKey.IsForIndex(
            bindingConflict, DomainIndexNames.BindingTechnicalName, Bindings.CollectionNamespace.CollectionName));
    }

    private IMongoCollection<TenantDomainZone> Zones =>
        fixture.Database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);

    private IMongoCollection<HostnameBinding> Bindings =>
        fixture.Database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    private static TenantDomainZone Zone() => TenantDomainZoneFactory.CreateManagedReservation(
        Id(), Id(), "company", DnsName.Create("example.test"), "company-zone",
        new TenantDomainZoneFactory.CreateOptions(Id(), DateTime.UtcNow));

    private static HostnameBinding Binding(TenantDomainZone zone) => HostnameBindingFactory.CreateCustom(
        zone.TenantId, zone.Id, zone.DeploymentTargetId, ResourceTypeId.Create("pole/web-service"),
        Id(), Id(), "api", DnsName.Create(zone.Domain), "api-alias",
        new HostnameBindingFactory.CreateOptions(Id(), DateTime.UtcNow));

    private static string Id() => ObjectId.GenerateNewId().ToString();
}
