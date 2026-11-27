// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Migrations;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Observation;

[Collection(IntegrationTestCollection.Global)]
public sealed class ObservationIndexContractTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    public override Task InitializeAsync() => base.InitializeAsync();
    public override Task DisposeAsync() => base.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migrations_create_expected_observation_index_fields_names_and_expiry(bool storage)
    {
        if (storage)
            await new StorageObservationIndexMigration().Up(Fixture.Database);
        else
            await new ResourceDeploymentObservationIndexMigration().Up(Fixture.Database);

        var demands = storage
            ? MongoStorageObservationStore.DemandsCollection
            : MongoResourceDeploymentObservationStore.DemandsCollection;
        var controls = storage
            ? MongoStorageObservationStore.ControlsCollection
            : MongoResourceDeploymentObservationStore.ControlsCollection;
        var snapshots = storage
            ? MongoStorageObservationStore.SnapshotsCollection
            : MongoResourceDeploymentObservationStore.SnapshotsCollection;
        var prefix = storage ? "StorageObservation" : "Observation";
        var active = storage
            ? new BsonDocument { { "DeploymentTargetId", 1 }, { "ExpiresAtUtc", 1 } }
            : new BsonDocument { { "TenantId", 1 }, { "DeploymentId", 1 }, { "ExpiresAtUtc", 1 } };

        await AssertIndex(demands, $"IX_{prefix}Demands_Active", active);
        await AssertIndex(demands, $"TTL_{prefix}Demands", new BsonDocument("ExpiresAtUtc", 1), expires: true);
        await AssertIndex(
            controls,
            $"IX_{prefix}Controls_Reaping",
            new BsonDocument { { "IdleSinceUtc", 1 }, { "UpdatedAtUtc", 1 } });
        await AssertIndex(snapshots, $"TTL_{prefix}Snapshots", new BsonDocument("ExpiresAtUtc", 1), expires: true);
    }

    private async Task AssertIndex(
        string collection,
        string name,
        BsonDocument keys,
        bool expires = false)
    {
        using var cursor = await Fixture.Database.GetCollection<BsonDocument>(collection).Indexes.ListAsync();
        var index = Assert.Single(await cursor.ToListAsync(), item => item["name"].AsString == name);

        Assert.Equal(keys, index["key"].AsBsonDocument);
        if (expires)
            Assert.Equal(0, index["expireAfterSeconds"].ToInt64());
        else
            Assert.False(index.Contains("expireAfterSeconds"));
    }
}
