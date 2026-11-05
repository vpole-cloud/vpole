using MongoDB.Driver;
using Pole.Features.StorageProfiles.Domain.Observation;
using Pole.Features.StorageProfiles.Application.Observation;

namespace Pole.Features.StorageProfiles.Application.Mongo;

internal static class StorageObservationIndexes
{
    internal static async Task CreateAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        await database.GetCollection<StorageObservationDemandDocument>(MongoStorageObservationStore.DemandsCollection)
            .Indexes
            .CreateManyAsync(
                [
                    new(Builders<StorageObservationDemandDocument>.IndexKeys
                            .Ascending(item => item.DeploymentTargetId).Ascending(item => item.ExpiresAtUtc),
                        new CreateIndexOptions { Name = "IX_StorageObservationDemands_Active" }),
                    new(Builders<StorageObservationDemandDocument>.IndexKeys.Ascending(item => item.ExpiresAtUtc),
                        new CreateIndexOptions { Name = "TTL_StorageObservationDemands", ExpireAfter = TimeSpan.Zero })
                ],
                ct);
        await database.GetCollection<StorageObservationControlDocument>(MongoStorageObservationStore.ControlsCollection)
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<StorageObservationControlDocument>(
                    Builders<StorageObservationControlDocument>.IndexKeys
                        .Ascending(item => item.IdleSinceUtc).Ascending(item => item.UpdatedAtUtc),
                    new CreateIndexOptions { Name = "IX_StorageObservationControls_Reaping" }),
                cancellationToken: ct);
        await database.GetCollection<StorageObservationSnapshotDocument>(
                MongoStorageObservationStore.SnapshotsCollection).Indexes
            .CreateOneAsync(
                new CreateIndexModel<StorageObservationSnapshotDocument>(
                    Builders<StorageObservationSnapshotDocument>.IndexKeys.Ascending(item => item.ExpiresAtUtc),
                    new CreateIndexOptions { Name = "TTL_StorageObservationSnapshots", ExpireAfter = TimeSpan.Zero }),
                cancellationToken: ct);
    }
}
