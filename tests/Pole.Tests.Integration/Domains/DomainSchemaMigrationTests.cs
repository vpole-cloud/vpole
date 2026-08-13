using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Domains.Application.Migrations;
using Pole.Features.Domains.Domain;
using Pole.Dns;
using Pole.Workloads;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Name)]
public sealed class DomainSchemaMigrationTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Migrations_create_strict_collections_and_required_indexes()
    {
        await new TenantDomainZoneSchemaValidationMigration().Up(fixture.Database);
        await new HostnameBindingSchemaValidationMigration().Up(fixture.Database);
        await new DomainIndexesMigration().Up(fixture.Database);

        await new MongoDocumentContractTester<BsonDocument>(fixture.Database, "TenantDomainZones")
            .AssertStrictErrorValidationAsync();
        await new MongoDocumentContractTester<BsonDocument>(fixture.Database, "HostnameBindings")
            .AssertStrictErrorValidationAsync();

        var now = DateTime.UtcNow;
        var tenantId = ObjectId.GenerateNewId().ToString();
        var targetId = ObjectId.GenerateNewId().ToString();
        var zone = TenantDomainZoneFactory.CreateManagedReservation(
            tenantId,
            targetId,
            "company",
            DnsName.Create("example.com"),
            new TenantDomainZoneFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        var binding = HostnameBindingFactory.CreateCustom(
            tenantId,
            zone.Id,
            targetId,
            ResourceTypeId.Create("pole/web-service"),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            "api",
            DnsName.Create(zone.Domain),
            new HostnameBindingFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        await new MongoDocumentContractTester<TenantDomainZone>(fixture.Database, "TenantDomainZones")
            .AssertRejectsAsync(zone, document => document["Unexpected"] = true);
        await new MongoDocumentContractTester<HostnameBinding>(fixture.Database, "HostnameBindings")
            .AssertRejectsAsync(binding, document => document["Unexpected"] = true);

        var zoneIndexes = (await fixture.Database.GetCollection<BsonDocument>("TenantDomainZones")
                .Indexes.List().ToListAsync())
            .Select(index => index["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);
        var bindingIndexes = (await fixture.Database.GetCollection<BsonDocument>("HostnameBindings")
                .Indexes.List().ToListAsync())
            .Select(index => index["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("UX_TenantDomainZones_Target_Domain", zoneIndexes);
        Assert.Contains("UX_TenantDomainZones_Tenant_Target_Generated", zoneIndexes);
        Assert.Contains("UX_HostnameBindings_Hostname", bindingIndexes);
        Assert.Contains("UX_HostnameBindings_Tenant_Deployment_Generated", bindingIndexes);
        Assert.Contains("IX_HostnameBindings_Zone", bindingIndexes);
    }

    [Fact]
    public async Task Schema_migrations_upgrade_existing_collections_with_collmod()
    {
        await fixture.Database.CreateCollectionAsync("TenantDomainZones");
        await fixture.Database.CreateCollectionAsync("HostnameBindings");

        await new TenantDomainZoneSchemaValidationMigration().Up(fixture.Database);
        await new HostnameBindingSchemaValidationMigration().Up(fixture.Database);

        await new MongoDocumentContractTester<BsonDocument>(fixture.Database, "TenantDomainZones")
            .AssertStrictErrorValidationAsync();
        await new MongoDocumentContractTester<BsonDocument>(fixture.Database, "HostnameBindings")
            .AssertStrictErrorValidationAsync();
    }
}
