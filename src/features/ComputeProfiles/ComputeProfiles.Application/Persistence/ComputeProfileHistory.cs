using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

public sealed record ComputeProfileRevision(
    [property: BsonId] string Id,
    string ComputeProfileId,
    long Revision,
    ComputeProfile Snapshot);

internal static class ComputeProfileRevisionFilters
{
    public static FilterDefinition<ComputeProfileRevision> Identity(string id, long revision) =>
        Builders<ComputeProfileRevision>.Filter.Eq(item => item.Id, $"{id}/{revision}");
}

internal sealed class ComputeProfileHistory(IMongoCollection<ComputeProfileRevision> revisions)
{
    public Task StoreAsync(ComputeProfile profile, IClientSessionHandle session, CancellationToken ct) =>
        StoreManyAsync([profile], session, ct);

    public Task StoreManyAsync(IReadOnlyCollection<ComputeProfile> profiles, IClientSessionHandle session, CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Profile history must share the catalog transaction.");
        if (profiles.Count == 0) return Task.CompletedTask;
        var writes = profiles.DistinctBy(profile => (profile.Id, profile.Revision)).Select(profile =>
            (WriteModel<ComputeProfileRevision>)new UpdateOneModel<ComputeProfileRevision>(
                ComputeProfileRevisionFilters.Identity(profile.Id, profile.Revision),
                Builders<ComputeProfileRevision>.Update.SetOnInsert(item => item.ComputeProfileId, profile.Id)
                    .SetOnInsert(item => item.Revision, profile.Revision).SetOnInsert(item => item.Snapshot, profile))
            { IsUpsert = true }).ToArray();
        return revisions.BulkWriteAsync(session, writes, cancellationToken: ct);
    }
}
