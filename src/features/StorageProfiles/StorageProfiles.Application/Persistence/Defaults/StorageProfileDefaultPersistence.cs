using MongoDB.Driver;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Persistence.Defaults;

internal static class StorageProfileDefaultPersistence
{
    public static Task DemoteOtherDefaultsAsync(
        IMongoCollection<StorageProfile> storageProfiles,
        IClientSessionHandle session,
        string? exceptProfileId,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var filter = exceptProfileId is null
            ? StorageProfileFilters.Default()
            : StorageProfileFilters.OtherDefaultsExcept(exceptProfileId);
        return storageProfiles.UpdateManyAsync(
            session,
            filter,
            Builders<StorageProfile>.Update
                .Set(profile => profile.IsDefault, false)
                .Inc(profile => profile.Revision, 1)
                .Max(profile => profile.UpdatedAtUtc, updatedAtUtc),
            cancellationToken: cancellationToken);
    }
}
