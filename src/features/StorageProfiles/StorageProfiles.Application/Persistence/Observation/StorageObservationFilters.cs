using Pole.Features.StorageProfiles.Domain.Observation;
using MongoDB.Driver;
using Pole.Database.Observation;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Persistence.Observation;

internal static class StorageObservationFilters
{
    internal static FilterDefinition<StorageObservationControlDocument> ForControlScope(MongoObservationScope scope) =>
        Builders<StorageObservationControlDocument>.Filter.Eq(item => item.Id, scope.Id);

    internal static FilterDefinition<StorageObservationDemandDocument> ForDemandScope(MongoObservationScope scope) =>
        Builders<StorageObservationDemandDocument>.Filter.Eq(item => item.DeploymentTargetId, scope.Id);

    internal static FilterDefinition<StorageObservationSnapshotDocument> ForSnapshotScope(MongoObservationScope scope) =>
        Builders<StorageObservationSnapshotDocument>.Filter.Eq(item => item.Id, scope.Id);

    internal static FilterDefinition<StorageObservationControlDocument> ForControlContext(
        StorageObservationContext context) =>
        Builders<StorageObservationControlDocument>.Filter.Eq(item => item.ConnectionVersion, context.ConnectionVersion);

    internal static FilterDefinition<StorageObservationSnapshotDocument> ForSnapshotContext(
        StorageObservationContext context) =>
        Builders<StorageObservationSnapshotDocument>.Filter.Eq(item => item.ConnectionVersion,
            context.ConnectionVersion);
}
