using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.ResourceDeployments.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

[Collection(IntegrationTestCollection.Global3)]
public sealed class ComputeProfileSchemaValidationTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    private const string CollectionName = "ComputeProfiles";

    public override Task InitializeAsync() => base.InitializeAsync();

    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task Migration_creates_strict_collection_that_accepts_current_domain_shape()
    {
        await new ComputeProfileSchemaValidationMigration().Up(Fixture.Database);
        var target = ComputeProfileTestData.CreateTarget();
        var contract = CreateContract();

        await contract.AssertStrictErrorValidationAsync();
        await contract.AssertAcceptsAsync(
        [
            ComputeProfileTestData.CreateProfile(target.Id, tenantAccess: AllTenantsComputeProfileAccess.Create()),
            ComputeProfileTestData.CreateProfile(
                target.Id,
                tenantAccess: SelectedTenantsComputeProfileAccess.Create([ComputeProfileTestData.TenantId])),
        ]);
    }

    [Fact]
    public async Task Migration_updates_existing_collection_and_rejects_missing_access_state()
    {
        await Fixture.Database.CreateCollectionAsync(CollectionName);
        await new ComputeProfileSchemaValidationMigration().Up(Fixture.Database);
        var profile = ComputeProfileTestData.CreateProfile(ComputeProfileTestData.CreateTarget().Id);
        var contract = CreateContract();

        await contract.AssertStrictErrorValidationAsync();
        await contract.AssertRejectsAsync(profile, document => document.Remove(nameof(ComputeProfile.TenantAccess)));
    }

    [Fact]
    public async Task Validator_rejects_invalid_selected_tenant_access_shape()
    {
        await new ComputeProfileSchemaValidationMigration().Up(Fixture.Database);
        var profile = ComputeProfileTestData.CreateProfile(
            ComputeProfileTestData.CreateTarget().Id,
            tenantAccess: SelectedTenantsComputeProfileAccess.Create([ComputeProfileTestData.TenantId]));

        await CreateContract()
            .AssertRejectsAsync(
                profile,
                document => document[nameof(ComputeProfile.TenantAccess)]
                    .AsBsonDocument
                    .Remove(nameof(SelectedTenantsComputeProfileAccess.TenantIds)));
    }

    [Fact]
    public async Task Index_migrations_create_default_availability_and_reference_indexes()
    {
        await new ComputeProfileSchemaValidationMigration().Up(Fixture.Database);
        await new ComputeProfileIndexesMigration().Up(Fixture.Database);
        await new ResourceDeploymentComputeProfileIndexMigration().Up(Fixture.Database);

        var profileIndexNames
            = (await Fixture.Database.GetCollection<BsonDocument>(CollectionName).Indexes.List().ToListAsync())
            .Select(index => index["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("UX_ComputeProfiles_Default", profileIndexNames);
        Assert.Contains("IX_ComputeProfiles_List", profileIndexNames);
        Assert.Contains("IX_ComputeProfiles_Target_Availability", profileIndexNames);

        var deploymentIndexNames
            = (await Fixture.Database.GetCollection<BsonDocument>("ResourceDeployments").Indexes.List().ToListAsync())
            .Select(index => index["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("IX_ResourceDeployments_ComputeProfile", deploymentIndexNames);
    }

    private MongoDocumentContractTester<ComputeProfile> CreateContract() => new(Fixture.Database, CollectionName);
}
