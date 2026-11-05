using Pole.Features.StorageProfiles.Application.Mongo;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Defaults;

internal sealed record StorageProfileDefaultsDemoted;

internal union DemoteStorageProfileDefaultsOutcome(
    StorageProfileDefaultsDemoted,
    StorageProfileRevisionConflict);

internal static class StorageProfileDefaultPersistence
{
    internal const int DefaultDemotionBatchSize = 128;

    public static async Task<DemoteStorageProfileDefaultsOutcome> DemoteOtherDefaultsAsync(
        IMongoCollection<StorageProfile> storageProfiles,
        StorageProfileHistory history,
        IClientSessionHandle session,
        string? exceptProfileId,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var filter = exceptProfileId is null
            ? StorageProfileFilters.Default()
            : StorageProfileFilters.OtherDefaultsExcept(exceptProfileId);
        while (true)
        {
            var defaults = await storageProfiles.Find(session, filter).SortBy(profile => profile.Id)
                .Limit(DefaultDemotionBatchSize).ToListAsync(cancellationToken);
            if (defaults.Count == 0)
                break;

            var replacements = defaults.Select(current => current with
            {
                IsDefault = false,
                Revision = checked(current.Revision + 1),
                UpdatedAtUtc = current.UpdatedAtUtc > updatedAtUtc ? current.UpdatedAtUtc : updatedAtUtc
            }).ToArray();
            var writes = defaults.Zip(replacements, (current, replacement) =>
                (WriteModel<StorageProfile>)new ReplaceOneModel<StorageProfile>(
                    StorageProfileFilters.AtSnapshot(current), replacement)).ToArray();
            var result = await storageProfiles.BulkWriteAsync(session, writes, cancellationToken: cancellationToken);
            if (result.MatchedCount != defaults.Count)
                return new StorageProfileRevisionConflict(exceptProfileId ?? defaults[0].Id);

            await history.StoreManyAsync(defaults.Concat(replacements).ToArray(), session, cancellationToken);
            if (defaults.Count < DefaultDemotionBatchSize)
                break;
        }

        return new StorageProfileDefaultsDemoted();
    }
}
