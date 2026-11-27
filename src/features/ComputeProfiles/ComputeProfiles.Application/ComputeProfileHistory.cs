// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Mongo;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application;

internal sealed class ComputeProfileHistory(
    IMongoCollection<ComputeProfileRevision> revisions
)
{
    public Task StoreAsync(
        ComputeProfile profile,
        IClientSessionHandle session,
        CancellationToken ct) => StoreManyAsync([profile], session, ct);

    public Task StoreManyAsync(
        IReadOnlyCollection<ComputeProfile> profiles,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Profile history must share the catalog transaction.");
        if (profiles.Count == 0)
            return Task.CompletedTask;

        var writes = profiles.DistinctBy(profile => (profile.Id, profile.Revision))
            .Select(profile =>
            {
                var revision = ComputeProfileRevisionFactory.Create(profile);
                return (WriteModel<ComputeProfileRevision>)new UpdateOneModel<ComputeProfileRevision>(
                    ComputeProfileRevisionFilters.Identity(revision.ComputeProfileId, revision.Revision),
                    Builders<ComputeProfileRevision>.Update
                        .SetOnInsert(item => item.ComputeProfileId, revision.ComputeProfileId)
                        .SetOnInsert(item => item.Revision, revision.Revision)
                        .SetOnInsert(item => item.Snapshot, revision.Snapshot)) { IsUpsert = true };
            })
            .ToArray();
        return revisions.BulkWriteAsync(session, writes, cancellationToken: ct);
    }
}
