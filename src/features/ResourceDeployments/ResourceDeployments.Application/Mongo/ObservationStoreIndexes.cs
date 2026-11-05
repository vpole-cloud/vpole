using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Domain.Observation;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Observation;

namespace Pole.Features.ResourceDeployments.Application.Mongo;

internal static class ObservationStoreIndexes
{
    public static async Task CreateAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        await database.GetCollection<ObservationDemandDocument>(
                MongoResourceDeploymentObservationStore.DemandsCollection).Indexes
            .CreateManyAsync(
                [
                    new(Builders<ObservationDemandDocument>.IndexKeys
                            .Ascending(item => item.TenantId).Ascending(item => item.DeploymentId)
                            .Ascending(item => item.ExpiresAtUtc),
                        new CreateIndexOptions { Name = "IX_ObservationDemands_Active" }),
                    new(Builders<ObservationDemandDocument>.IndexKeys.Ascending(item => item.ExpiresAtUtc),
                        new CreateIndexOptions { Name = "TTL_ObservationDemands", ExpireAfter = TimeSpan.Zero })
                ],
                ct);
        await database.GetCollection<ObservationControlDocument>(
                MongoResourceDeploymentObservationStore.ControlsCollection).Indexes
            .CreateOneAsync(
                new CreateIndexModel<ObservationControlDocument>(
                    Builders<ObservationControlDocument>.IndexKeys
                        .Ascending(item => item.IdleSinceUtc).Ascending(item => item.UpdatedAtUtc),
                    new CreateIndexOptions { Name = "IX_ObservationControls_Reaping" }),
                cancellationToken: ct);
        await database.GetCollection<ObservationSnapshotDocument>(
                MongoResourceDeploymentObservationStore.SnapshotsCollection).Indexes
            .CreateOneAsync(
                new CreateIndexModel<ObservationSnapshotDocument>(
                    Builders<ObservationSnapshotDocument>.IndexKeys.Ascending(item => item.ExpiresAtUtc),
                    new CreateIndexOptions { Name = "TTL_ObservationSnapshots", ExpireAfter = TimeSpan.Zero }),
                cancellationToken: ct);
    }
}
