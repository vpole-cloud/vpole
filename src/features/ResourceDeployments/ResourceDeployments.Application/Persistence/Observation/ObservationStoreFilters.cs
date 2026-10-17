using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Observation;

internal static class ObservationStoreFilters
{
    internal static FilterDefinition<ObservationSnapshotDocument> ForSnapshotContext(
        ResourceDeploymentObservationContext context) =>
        Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.Pin, ObservationValueMappings.Pin(context));
}
