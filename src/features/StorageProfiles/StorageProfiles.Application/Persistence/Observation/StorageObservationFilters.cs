using MongoDB.Driver;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Persistence.Observation;

internal static class StorageObservationFilters
{
    internal static FilterDefinition<StorageObservationSnapshotDocument> ForSnapshotContext(
        StorageObservationContext context) =>
        Builders<StorageObservationSnapshotDocument>.Filter.Eq(item => item.ConnectionVersion,
            context.ConnectionVersion);
}
