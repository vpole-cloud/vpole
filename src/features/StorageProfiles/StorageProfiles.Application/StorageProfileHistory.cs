using Pole.Features.StorageProfiles.Application.Mongo;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application;

internal sealed class StorageProfileHistory(IMongoCollection<StorageProfileRevision> revisions)
{
    public Task StoreAsync(StorageProfile profile, IClientSessionHandle session, CancellationToken ct) =>
        StoreManyAsync([profile], session, ct);

    public Task StoreManyAsync(
        IReadOnlyCollection<StorageProfile> profiles,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Profile history must share the catalog transaction.");

        if (profiles.Count == 0)
            return Task.CompletedTask;

        var writes = profiles.DistinctBy(profile => (profile.Id, profile.Revision)).Select(profile =>
        {
            var revision = StorageProfileRevisionFactory.Create(profile);
            return (WriteModel<StorageProfileRevision>)new UpdateOneModel<StorageProfileRevision>(
                StorageProfileRevisionFilters.Identity(revision.StorageProfileId, revision.Revision),
                Builders<StorageProfileRevision>.Update
                    .SetOnInsert(item => item.StorageProfileId, revision.StorageProfileId)
                    .SetOnInsert(item => item.Revision, revision.Revision)
                    .SetOnInsert(item => item.Snapshot, revision.Snapshot))
            { IsUpsert = true };
        }).ToArray();

        return revisions.BulkWriteAsync(session, writes, cancellationToken: ct);
    }
}
