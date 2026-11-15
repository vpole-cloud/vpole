using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.SeaweedFS.Application.Mongo;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.SeaweedFS.Events;

namespace Pole.Features.SeaweedFS.Application.Services;

public sealed class SeaweedFSAudit(
    IMongoCollection<SeaweedFSService> services,
    IMongoCollection<SeaweedFSBucket> buckets,
    MongoRunTransaction transactions,
    FenceRegisteredResource fence,
    StoreOutboxWrites outbox
)
{
    public Task<bool> RevealAsync(
        string tenant,
        string id,
        string? bucketId,
        long resourceRevision,
        string actor,
        CancellationToken ct) => transactions.InvokeOutcomeAsync<bool>(
        async (tx, token) =>
        {
            if (await fence(new(tenant, ResourceTypeId.Create("pole/seaweedfs"), id), tx.Session, token) is null)
                return tx.Abort(false);
            var service = await services.Find(tx.Session, SeaweedFSFilters.ById(tenant, id)).FirstOrDefaultAsync(token);
            if (service?.Lifecycle is not ActiveSeaweedFSLifecycle)
                return tx.Abort(false);
            if (bucketId is null && service.Revision != resourceRevision)
                return tx.Abort(false);
            if (bucketId is not null)
            {
                var bucket = await buckets.Find(tx.Session, SeaweedFSFilters.BucketById(tenant, id, bucketId!))
                    .FirstOrDefaultAsync(token);
                if (bucket?.Lifecycle is not ActiveSeaweedFSLifecycle || bucket.Revision != resourceRevision)
                    return tx.Abort(false);
            }

            await outbox(
                tx.Session,
                [
                    OutboxWrite.Create(
                        new SeaweedFSCredentialsRevealedEvent(id, bucketId, actor, EventCore.Create(tenant)))
                ],
                token);
            return tx.Commit(true);
        },
        ct);
}
