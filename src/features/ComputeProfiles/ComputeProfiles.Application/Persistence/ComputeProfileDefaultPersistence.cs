using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal static class ComputeProfileDefaultPersistence
{
    public static Task DemoteOtherDefaultsAsync(
        IMongoCollection<ComputeProfile> computeProfiles,
        IClientSessionHandle session,
        string? exceptProfileId,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var filter = exceptProfileId is null
            ? ComputeProfileFilters.Default()
            : ComputeProfileFilters.OtherDefaultsExcept(exceptProfileId);

        return computeProfiles.UpdateManyAsync(
            session,
            filter,
            Builders<ComputeProfile>.Update
                .Set(profile => profile.IsDefault, false)
                .Inc(profile => profile.Revision, 1)
                .Max(profile => profile.UpdatedAtUtc, updatedAtUtc),
            cancellationToken: cancellationToken);
    }
}
