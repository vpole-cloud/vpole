using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.StorageProfiles.Application.Mongo;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Observation;

[Collection(IntegrationTestCollection.Name)]
public sealed class ObservationIndexContractTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_indexes_preserve_field_order_names_and_expiry(bool storage)
    {
        if (storage)
            await StorageObservationIndexes.CreateAsync(fixture.Database);
        else
            await ObservationStoreIndexes.CreateAsync(fixture.Database);

        var demands = storage ? MongoStorageObservationStore.DemandsCollection :
            MongoResourceDeploymentObservationStore.DemandsCollection;
        var controls = storage ? MongoStorageObservationStore.ControlsCollection :
            MongoResourceDeploymentObservationStore.ControlsCollection;
        var snapshots = storage ? MongoStorageObservationStore.SnapshotsCollection :
            MongoResourceDeploymentObservationStore.SnapshotsCollection;
        var prefix = storage ? "StorageObservation" : "Observation";
        var active = storage
            ? new BsonDocument { { "DeploymentTargetId", 1 }, { "ExpiresAtUtc", 1 } }
            : new BsonDocument { { "TenantId", 1 }, { "DeploymentId", 1 }, { "ExpiresAtUtc", 1 } };

        await AssertIndex(demands, $"IX_{prefix}Demands_Active", active);
        await AssertIndex(demands, $"TTL_{prefix}Demands", new("ExpiresAtUtc", 1), expires: true);
        await AssertIndex(controls, $"IX_{prefix}Controls_Reaping",
            new BsonDocument { { "IdleSinceUtc", 1 }, { "UpdatedAtUtc", 1 } });
        await AssertIndex(snapshots, $"TTL_{prefix}Snapshots", new("ExpiresAtUtc", 1), expires: true);
    }

    private async Task AssertIndex(string collection, string name, BsonDocument keys, bool expires = false)
    {
        using var cursor = await fixture.Database.GetCollection<BsonDocument>(collection).Indexes.ListAsync();
        var index = Assert.Single(await cursor.ToListAsync(), item => item["name"].AsString == name);

        Assert.Equal(keys, index["key"].AsBsonDocument);
        if (expires)
            Assert.Equal(0, index["expireAfterSeconds"].ToInt64());
        else
            Assert.False(index.Contains("expireAfterSeconds"));
    }
}
