using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Migrations;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

[Collection(IntegrationTestCollection.Name)]
public sealed class ComputeProfileSchemaValidationTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private const string CollectionName = "ComputeProfiles";

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Migration_creates_strict_collection_that_accepts_current_domain_shape()
    {
        await new ComputeProfileSchemaValidationMigration().Up(fixture.Database);
        var target = ComputeProfileTestData.CreateTarget();
        var contract = CreateContract();

        await contract.AssertStrictErrorValidationAsync();
        await contract.AssertAcceptsAsync(
        [
            ComputeProfileTestData.CreateProfile(
                target.Id,
                tenantAccess: AllTenantsComputeProfileAccess.Create()),
            ComputeProfileTestData.CreateProfile(
                target.Id,
                tenantAccess: SelectedTenantsComputeProfileAccess.Create(
                    [ComputeProfileTestData.TenantId]))
        ]);
    }

    [Fact]
    public async Task Migration_updates_existing_collection_and_rejects_missing_access_state()
    {
        await fixture.Database.CreateCollectionAsync(CollectionName);
        await new ComputeProfileSchemaValidationMigration().Up(fixture.Database);
        var profile = ComputeProfileTestData.CreateProfile(
            ComputeProfileTestData.CreateTarget().Id);
        var contract = CreateContract();

        await contract.AssertStrictErrorValidationAsync();
        await contract.AssertRejectsAsync(
            profile,
            document => document.Remove(nameof(ComputeProfile.TenantAccess)));
    }

    [Fact]
    public async Task Validator_rejects_invalid_selected_tenant_access_shape()
    {
        await new ComputeProfileSchemaValidationMigration().Up(fixture.Database);
        var profile = ComputeProfileTestData.CreateProfile(
            ComputeProfileTestData.CreateTarget().Id,
            tenantAccess: SelectedTenantsComputeProfileAccess.Create(
                [ComputeProfileTestData.TenantId]));

        await CreateContract().AssertRejectsAsync(
            profile,
            document => document[nameof(ComputeProfile.TenantAccess)]
                .AsBsonDocument
                .Remove(nameof(SelectedTenantsComputeProfileAccess.TenantIds)));
    }

    [Fact]
    public async Task Index_migrations_create_default_availability_and_reference_indexes()
    {
        await new ComputeProfileSchemaValidationMigration().Up(fixture.Database);
        await new ComputeProfileIndexesMigration().Up(fixture.Database);
        await new ComputeProfileResourceDeploymentIndexMigration().Up(fixture.Database);

        var profileIndexNames = (await fixture.Database
                .GetCollection<BsonDocument>(CollectionName)
                .Indexes.List()
                .ToListAsync())
            .Select(index => index["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("UX_ComputeProfiles_Default", profileIndexNames);
        Assert.Contains("IX_ComputeProfiles_List", profileIndexNames);
        Assert.Contains("IX_ComputeProfiles_Target_Availability", profileIndexNames);

        var deploymentIndexNames = (await fixture.Database
                .GetCollection<BsonDocument>("ResourceDeployments")
                .Indexes.List()
                .ToListAsync())
            .Select(index => index["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("IX_ResourceDeployments_ComputeProfile", deploymentIndexNames);
    }

    private MongoDocumentContractTester<ComputeProfile> CreateContract() =>
        new(fixture.Database, CollectionName);
}
