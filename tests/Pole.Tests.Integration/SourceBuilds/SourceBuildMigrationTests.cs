using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Migrations;
using Pole.Features.SourceBuilds.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

[Collection(IntegrationTestCollection.Global)]
public sealed class SourceBuildMigrationTests(MongoReplicaSetFixture fixture)
    : MongoIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Migrations_CreateTenantRegistryConfigurationAndBuildIndexes()
    {
        await new ContainerRegistryMigration().Up(Fixture.Database);
        await new SourceBuildMigration().Up(Fixture.Database);

        var registryProjectIndexes = await GetIndexesAsync("RegistryProjects");
        Assert.True(registryProjectIndexes
            .Single(index => index["name"] == "UX_RegistryProjects_Tenant_Provider")["unique"]
            .AsBoolean);

        var credentialIndexes = await GetIndexesAsync("RegistryCredentials");
        Assert.True(credentialIndexes
            .Single(index => index["name"] == "UX_RegistryCredentials_Project_Purpose")["unique"]
            .AsBoolean);

        var configurationIndexes = await GetIndexesAsync("SourceBuildConfigurations");
        Assert.True(configurationIndexes
            .Single(index => index["name"] == "UX_SourceBuildConfigurations_Tenant_WebService")["unique"]
            .AsBoolean);

        var buildIndexes = await GetIndexesAsync("SourceBuildJobs");
        Assert.Contains(
            buildIndexes,
            index => index["name"] == "IX_SourceBuildJobs_State_UpdatedAt");
        Assert.Contains(
            buildIndexes,
            index => index["name"] == "IX_SourceBuildJobs_Tenant_State_Release");
    }

    private async Task<IReadOnlyList<BsonDocument>> GetIndexesAsync(string collectionName)
    {
        var cursor = await Fixture.Database
            .GetCollection<BsonDocument>(collectionName)
            .Indexes
            .ListAsync();
        return await cursor.ToListAsync();
    }
}
