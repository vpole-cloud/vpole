using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Migrations;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Global)]
public sealed class ResourceDeploymentSchemaValidationTests(MongoReplicaSetFixture fixture)
    : MongoIntegrationTestBase(fixture)
{
    private const string CollectionName = "ResourceDeployments";
    private static readonly DateTime CreatedAtUtc =
        new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    public override Task InitializeAsync() => base.InitializeAsync();

    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task Migration_creates_strict_collection_for_multi_target_snapshot()
    {
        await new ResourceDeploymentSchemaValidationMigration().Up(Fixture.Database);
        var contract = new MongoDocumentContractTester<ResourceDeployment>(
            Fixture.Database,
            CollectionName);

        await contract.AssertStrictErrorValidationAsync();
        await contract.AssertAcceptsAsync([CreateDeployment()]);
        await contract.AssertRejectsAsync(
            CreateDeployment(),
            document => document.Remove(nameof(ResourceDeployment.ResourceRevision)));
        await contract.AssertRejectsAsync(
            CreateDeployment(),
            document => document.Remove(nameof(ResourceDeployment.FixedReplicas)));
    }

    [Fact]
    public async Task Migration_updates_existing_collection_with_strict_validation()
    {
        await Fixture.Database.CreateCollectionAsync(CollectionName);
        await new ResourceDeploymentSchemaValidationMigration().Up(Fixture.Database);
        var contract = new MongoDocumentContractTester<ResourceDeployment>(
            Fixture.Database,
            CollectionName);

        await contract.AssertStrictErrorValidationAsync();
        await contract.AssertRejectsAsync(
            CreateDeployment(),
            document => document["Unexpected"] = new BsonString("not allowed"));
    }

    [Fact]
    public async Task RecoveryMigration_indexes_state_update_time_and_identity()
    {
        await new ResourceDeploymentRecoveryIndexMigration().Up(Fixture.Database);
        var deployments = Fixture.Database
            .GetCollection<BsonDocument>(CollectionName);
        var indexes = await (await deployments.Indexes.ListAsync()).ToListAsync();

        var recoveryIndex = Assert.Single(indexes, index =>
            index["name"] == "IX_ResourceDeployments_State_Updated");
        Assert.Equal(
            new BsonDocument
            {
                ["State._t"] = 1,
                ["UpdatedAtUtc"] = 1,
                ["_id"] = 1
            },
            recoveryIndex["key"].AsBsonDocument);
    }

    private static ResourceDeployment CreateDeployment() =>
        ResourceDeploymentFactory.Create(
            "507f1f77bcf86cd799439011",
            ResourceTypeId.Create("pole/test-resource"),
            "507f1f77bcf86cd799439012",
            "507f1f77bcf86cd799439013",
            "507f1f77bcf86cd799439014",
            ResolvedResourcePlacement.Create(
                "507f1f77bcf86cd799439015",
                "507f1f77bcf86cd799439016",
                "pole-test-namespace",
                runtimeCellGeneration: 1,
                targetWorkloadConfigurationRevision: 1,
                ResolvedIngressConfiguration.Create(
                    "traefik",
                    "letsencrypt-prod",
                    "platform.example.test",
                    ["service.platform.example.test"]),
                storage: null,
                ResourceDeploymentTestData.Compute(),
                CreatedAtUtc),
            ResourcePlacementCapability.MultiTarget,
            runtimeCellReady: true,
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = "507f1f77bcf86cd799439017",
                CreatedAtUtc = CreatedAtUtc,
                ResourceRevision = 2,
                FixedReplicas = 3
            });
}
