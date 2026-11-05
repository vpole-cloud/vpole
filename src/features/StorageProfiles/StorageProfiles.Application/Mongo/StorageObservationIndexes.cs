using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.StorageProfiles.Application.Mongo;

internal static class StorageObservationIndexes
{
    internal static async Task CreateAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        await database.GetCollection<BsonDocument>(MongoStorageObservationStore.DemandsCollection).Indexes
            .CreateManyAsync(
                [
                    new(Builders<BsonDocument>.IndexKeys.Ascending("DeploymentTargetId").Ascending("ExpiresAtUtc"),
                        new CreateIndexOptions { Name = "IX_StorageObservationDemands_Active" }),
                    new(Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                        new CreateIndexOptions { Name = "TTL_StorageObservationDemands", ExpireAfter = TimeSpan.Zero })
                ],
                ct);
        await database.GetCollection<BsonDocument>(MongoStorageObservationStore.ControlsCollection).Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("IdleSinceUtc").Ascending("UpdatedAtUtc"),
                    new CreateIndexOptions { Name = "IX_StorageObservationControls_Reaping" }),
                cancellationToken: ct);
        await database.GetCollection<BsonDocument>(MongoStorageObservationStore.SnapshotsCollection).Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                    new CreateIndexOptions { Name = "TTL_StorageObservationSnapshots", ExpireAfter = TimeSpan.Zero }),
                cancellationToken: ct);
    }
}
