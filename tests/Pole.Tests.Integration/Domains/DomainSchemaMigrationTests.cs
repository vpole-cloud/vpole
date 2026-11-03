using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Domains.Migrations;
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
            "zone-" + MongoDB.Bson.ObjectId.GenerateNewId(),
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
            "binding-" + MongoDB.Bson.ObjectId.GenerateNewId(),
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
    public async Task Indexes_freeze_scalar_identity_discriminators_and_compound_key_order()
    {
        await new TenantDomainZoneSchemaValidationMigration().Up(fixture.Database);
        await new HostnameBindingSchemaValidationMigration().Up(fixture.Database);
        await new DomainIndexesMigration().Up(fixture.Database);

        foreach (var (collection, generatedIndex, parentField) in new[]
                 {
                     ("TenantDomainZones", "UX_TenantDomainZones_Tenant_Target_Generated", "DeploymentTargetId"),
                     ("HostnameBindings", "UX_HostnameBindings_Tenant_Deployment_Generated", "ResourceDeploymentId")
                 })
        {
            var indexes = await fixture.Database.GetCollection<BsonDocument>(collection).Indexes.List().ToListAsync();
            var generated = Assert.Single(indexes, index => index["name"] == generatedIndex);
            Assert.Equal(new BsonDocument { ["TenantId"] = 1, [parentField] = 1, ["Identity._t"] = 1 }, generated["key"]);
            Assert.True(generated["unique"].AsBoolean);
            Assert.Equal(new BsonDocument("Identity._t", "GeneratedDomainIdentity"), generated["partialFilterExpression"]);
            var named = Assert.Single(indexes, index => index["name"] == $"UX_{collection}_Tenant_TechnicalName");
            Assert.Equal(new BsonDocument { ["TenantId"] = 1, ["Identity.TechnicalName"] = 1 }, named["key"]);
            Assert.Equal(new BsonDocument("Identity._t", "NamedDomainIdentity"), named["partialFilterExpression"]);
        }
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
