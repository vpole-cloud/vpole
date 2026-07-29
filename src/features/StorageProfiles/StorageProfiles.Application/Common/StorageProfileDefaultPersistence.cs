using MongoDB.Driver;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Common;

internal static class StorageProfileDefaultPersistence
{
    public static Task DemoteOtherDefaultsAsync(
        IStorageProfileDbContext dbContext,
        IClientSessionHandle session,
        string? exceptProfileId,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var filter = Builders<StorageProfile>.Filter.Eq(
            profile => profile.IsDefault,
            true);
        if (exceptProfileId is not null)
        {
            filter &= Builders<StorageProfile>.Filter.Ne(
                profile => profile.Id,
                exceptProfileId);
        }

        return dbContext.StorageProfiles.UpdateManyAsync(
            session,
            filter,
            Builders<StorageProfile>.Update
                .Set(profile => profile.IsDefault, false)
                .Inc(profile => profile.Revision, 1)
                .Max(profile => profile.UpdatedAtUtc, updatedAtUtc),
            cancellationToken: cancellationToken);
    }
}
