using Pole.Features.ResourceDeployments.Domain.Observation;
using MongoDB.Driver;
using Pole.Database.Observation;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Observation;

internal static class ObservationStoreFilters
{
    internal static FilterDefinition<ObservationControlDocument> ForControlScope(MongoObservationScope scope) =>
        Builders<ObservationControlDocument>.Filter.Eq(item => item.Id, scope.Id) &
        Builders<ObservationControlDocument>.Filter.Eq(item => item.TenantId, scope.TenantId!);

    internal static FilterDefinition<ObservationDemandDocument> ForDemandScope(MongoObservationScope scope) =>
        Builders<ObservationDemandDocument>.Filter.Eq(item => item.DeploymentId, scope.Id) &
        Builders<ObservationDemandDocument>.Filter.Eq(item => item.TenantId, scope.TenantId!);

    internal static FilterDefinition<ObservationSnapshotDocument> ForSnapshotScope(MongoObservationScope scope) =>
        Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.Id, scope.Id) &
        Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.TenantId, scope.TenantId!);

    internal static FilterDefinition<ObservationControlDocument> ForControlContext(
        ResourceDeploymentObservationContext context) =>
        Builders<ObservationControlDocument>.Filter.Eq(item => item.Pin, ObservationValueMappings.Pin(context));

    internal static FilterDefinition<ObservationSnapshotDocument> ForSnapshotContext(
        ResourceDeploymentObservationContext context) =>
        Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.Pin, ObservationValueMappings.Pin(context));
}
