using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Persistence.Defaults;

internal sealed record ComputeProfileDefaultsDemoted;
internal union DemoteComputeProfileDefaultsOutcome(
    ComputeProfileDefaultsDemoted,
    ComputeProfileRevisionConflict);

internal static class ComputeProfileDefaultPersistence
{
    // Also bounds recovery of legacy catalogs before the unique-default index is installed.
    internal const int DefaultDemotionBatchSize = 128;

    public static async Task<DemoteComputeProfileDefaultsOutcome> DemoteOtherDefaultsAsync(
        IMongoCollection<ComputeProfile> computeProfiles,
        ComputeProfileHistory history,
        IClientSessionHandle session,
        string? exceptProfileId,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var filter = exceptProfileId is null
            ? ComputeProfileFilters.Default()
            : ComputeProfileFilters.OtherDefaultsExcept(exceptProfileId);
        while (true)
        {
            var defaults = await computeProfiles.Find(session, filter).SortBy(profile => profile.Id)
                .Limit(DefaultDemotionBatchSize).ToListAsync(cancellationToken);
            if (defaults.Count == 0) break;
            var replacements = defaults.Select(current => current with
            {
                IsDefault = false,
                Revision = checked(current.Revision + 1),
                UpdatedAtUtc = current.UpdatedAtUtc > updatedAtUtc ? current.UpdatedAtUtc : updatedAtUtc
            }).ToArray();
            var writes = defaults.Zip(replacements, (current, replacement) =>
                (WriteModel<ComputeProfile>)new ReplaceOneModel<ComputeProfile>(
                    ComputeProfileFilters.AtSnapshot(current), replacement)).ToArray();
            var result = await computeProfiles.BulkWriteAsync(session, writes, cancellationToken: cancellationToken);
            if (result.MatchedCount != defaults.Count)
                return new ComputeProfileRevisionConflict(exceptProfileId ?? defaults[0].Id);
            await history.StoreManyAsync(defaults.Concat(replacements).ToArray(), session, cancellationToken);
            if (defaults.Count < DefaultDemotionBatchSize) break;
        }
        return new ComputeProfileDefaultsDemoted();
    }
}
