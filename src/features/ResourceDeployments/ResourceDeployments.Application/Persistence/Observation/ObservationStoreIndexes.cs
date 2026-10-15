using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Observation;

internal static class ObservationStoreIndexes
{
    public static async Task CreateAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        await database.GetCollection<BsonDocument>(MongoResourceDeploymentObservationStore.DemandsCollection).Indexes
            .CreateManyAsync(
                [
                    new(Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("DeploymentId")
                            .Ascending("ExpiresAtUtc"),
                        new CreateIndexOptions { Name = "IX_ObservationDemands_Active" }),
                    new(Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                        new CreateIndexOptions { Name = "TTL_ObservationDemands", ExpireAfter = TimeSpan.Zero })
                ],
                ct);
        await database.GetCollection<BsonDocument>(MongoResourceDeploymentObservationStore.ControlsCollection).Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("IdleSinceUtc").Ascending("UpdatedAtUtc"),
                    new CreateIndexOptions { Name = "IX_ObservationControls_Reaping" }),
                cancellationToken: ct);
        await database.GetCollection<BsonDocument>(MongoResourceDeploymentObservationStore.SnapshotsCollection).Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                    new CreateIndexOptions { Name = "TTL_ObservationSnapshots", ExpireAfter = TimeSpan.Zero }),
                cancellationToken: ct);
    }
}
