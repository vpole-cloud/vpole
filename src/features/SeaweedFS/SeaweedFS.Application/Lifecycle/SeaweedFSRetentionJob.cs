using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Features.Postgres.Contracts;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Mongo;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.SeaweedFS.Events;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.SeaweedFS.Application.Lifecycle;

public sealed record SeaweedFSRetentionProcessed;

public sealed record SeaweedFSRetentionConflict;

public union SeaweedFSRetentionOutcome(SeaweedFSRetentionProcessed, SeaweedFSRetentionConflict);

/// <summary>Retained documents are durable tombstones; external deletion always precedes finalization.</summary>
public sealed class SeaweedFSRetentionJob(
    IMongoCollection<SeaweedFSService> services,
    IMongoCollection<SeaweedFSBucket> buckets,
    MongoRunTransaction transactions,
    PurgeSeaweedFSBucketData purgeBucket,
    PurgePostgresManagedDatabase purgeMetadata,
    ReleaseManagedSecret releaseSecret,
    LoadResourceDeployment loadPlacement,
    RequestExactResourceDeploymentDeletion deleteDeployment,
    GetResourceDeploymentFinalizationReadiness readiness,
    PurgeDeletedResourceDeployments purgeDeployments,
    IResourceRegistrar registrar,
    FenceRegisteredResource fence,
    IQuotaUsageRelease quotaRelease,
    StoreOutboxWrites outbox,
    TimeProvider clock,
    ILogger<SeaweedFSRetentionJob> logger
)
{
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        string? after = null;
        // Background discovery intentionally spans tenants; each mutation uses the persisted tenant.
        while (true)
        {
            var page = await services.Find(SeaweedFSFilters.DueServices(now, after))
                .SortBy(x => x.Id)
                .Limit(100)
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
                break;
            foreach (var service in page)
                await IsolatedAsync(
                    service.TenantId,
                    service.Id,
                    () => BeginPurgeAsync(service, cancellationToken),
                    cancellationToken);
            after = page[^1].Id;
        }

        after = null;
        while (true)
        {
            var page = await buckets.Find(SeaweedFSFilters.DueBuckets(now, after))
                .SortBy(x => x.Id)
                .Limit(100)
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
                break;
            foreach (var bucket in page)
                await IsolatedAsync(
                    bucket.TenantId,
                    bucket.Id,
                    () => PurgeBucketAsync(bucket, cancellationToken),
                    cancellationToken);
            after = page[^1].Id;
        }

        after = null;
        while (true)
        {
            var page = await services.Find(SeaweedFSFilters.DueServices(now, after))
                .SortBy(x => x.Id)
                .Limit(100)
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
                break;
            foreach (var service in page)
                await IsolatedAsync(
                    service.TenantId,
                    service.Id,
                    () => FinalizeServiceAsync(service.TenantId, service.Id, cancellationToken),
                    cancellationToken);
            after = page[^1].Id;
        }
    }

    private async Task IsolatedAsync(
        string tenantId,
        string resourceId,
        Func<Task> operation,
        CancellationToken ct)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            ct.ThrowIfCancellationRequested();
            // Provider diagnostics can contain connection strings or response bodies; log identity and type only.
            logger.LogError(
                "SeaweedFS retention failed for tenant {TenantId}, resource {ResourceId}: {ErrorType}",
                tenantId,
                resourceId,
                error.GetType().FullName);
        }
    }

    private Task<SeaweedFSRetentionOutcome> BeginPurgeAsync(SeaweedFSService service, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<SeaweedFSRetentionOutcome>(
            async (tx, token) =>
            {
                if (service.Lifecycle is not DeletingSeaweedFSLifecycle deleting)
                    return tx.Commit(new SeaweedFSRetentionProcessed());
                if (await fence(
                        new(service.TenantId, SeaweedFSResourceDeployment.ResourceType, service.Id),
                        tx.Session,
                        token) is null)
                    return tx.Abort(new SeaweedFSRetentionConflict());
                var replacement = service with
                {
                    Revision = checked(service.Revision + 1),
                    Lifecycle = new PurgingSeaweedFSLifecycle(deleting.RequestedAtUtc, deleting.DeletionDeadlineUtc)
                };
                var replaced = await services.ReplaceOneAsync(
                    tx.Session,
                    SeaweedFSFilters.AtRevision(service),
                    replacement,
                    cancellationToken: token);
                if (replaced.ModifiedCount != 1)
                    return tx.Abort(new SeaweedFSRetentionConflict());
                var children = await buckets.Find(tx.Session, SeaweedFSFilters.Buckets(service.TenantId, service.Id))
                    .ToListAsync(token);
                foreach (var bucket in children.Where(x => x.Lifecycle is not DeletedSeaweedFSLifecycle))
                {
                    if (bucket.Lifecycle is DeletingSeaweedFSLifecycle retained &&
                        retained.DeletionDeadlineUtc > clock.GetUtcNow().UtcDateTime)
                        continue;
                    var lifecycle = bucket.Lifecycle is DeletingSeaweedFSLifecycle childDeletion
                        ? new PurgingSeaweedFSLifecycle(childDeletion.RequestedAtUtc, childDeletion.DeletionDeadlineUtc)
                        : replacement.Lifecycle;
                    var purging = bucket with { Revision = checked(bucket.Revision + 1), Lifecycle = lifecycle };
                    if ((await buckets.ReplaceOneAsync(
                            tx.Session,
                            SeaweedFSFilters.BucketAtRevision(bucket),
                            purging,
                            cancellationToken: token)).ModifiedCount != 1)
                        return tx.Abort(new SeaweedFSRetentionConflict());
                }

                await ChangedAsync(tx.Session, replacement, "PurgeStarted", null, token);
                return tx.Commit(new SeaweedFSRetentionProcessed());
            },
            ct);

    private async Task PurgeBucketAsync(SeaweedFSBucket bucket, CancellationToken ct)
    {
        if (bucket.Lifecycle is DeletingSeaweedFSLifecycle deleting)
        {
            if (deleting.DeletionDeadlineUtc > clock.GetUtcNow().UtcDateTime)
                return;
            var replacement = bucket with
            {
                Revision = checked(bucket.Revision + 1),
                Lifecycle = new PurgingSeaweedFSLifecycle(deleting.RequestedAtUtc, deleting.DeletionDeadlineUtc)
            };
            var transitioned = await transactions.InvokeOutcomeAsync<SeaweedFSRetentionOutcome>(
                async (tx, token) =>
                {
                    var service = await services.Find(
                            tx.Session,
                            SeaweedFSFilters.ById(bucket.TenantId, bucket.SeaweedFSId))
                        .FirstOrDefaultAsync(token);
                    if (service is null)
                        return tx.Abort(new SeaweedFSRetentionConflict());
                    if ((await buckets.ReplaceOneAsync(
                            tx.Session,
                            SeaweedFSFilters.BucketAtRevision(bucket),
                            replacement,
                            cancellationToken: token)).ModifiedCount != 1)
                        return tx.Abort(new SeaweedFSRetentionConflict());
                    await ChangedAsync(tx.Session, service, "BucketPurgeStarted", bucket.Id, token);
                    return tx.Commit(new SeaweedFSRetentionProcessed());
                },
                ct);
            if (transitioned is SeaweedFSRetentionConflict)
                return;
            bucket = replacement;
        }

        if (bucket.Lifecycle is not PurgingSeaweedFSLifecycle)
            return;
        var result = await purgeBucket(new(bucket.TenantId, bucket.SeaweedFSId, bucket.Id, bucket.Name), ct);
        if (result is SeaweedFSBucketPurgeRetryableFailure)
            return;
        await transactions.InvokeOutcomeAsync<SeaweedFSRetentionOutcome>(
            async (tx, token) =>
            {
                var tombstone = bucket with
                {
                    Revision = checked(bucket.Revision + 1),
                    Lifecycle = new DeletedSeaweedFSLifecycle(clock.GetUtcNow().UtcDateTime), ReservesName = false
                };
                if ((await buckets.ReplaceOneAsync(
                        tx.Session,
                        SeaweedFSFilters.BucketAtRevision(bucket),
                        tombstone,
                        cancellationToken: token)).ModifiedCount != 1)
                    return tx.Abort(new SeaweedFSRetentionConflict());
                var released = await releaseSecret(
                    new(bucket.TenantId, SeaweedFSCredentials.BucketOwner(bucket.SeaweedFSId, bucket.Id)),
                    tx.Session,
                    token);
                if (released is not (ManagedSecretMutationSucceeded or ManagedSecretUnavailable))
                    return tx.Abort(new SeaweedFSRetentionConflict());
                var service = await services
                    .Find(tx.Session, SeaweedFSFilters.ById(bucket.TenantId, bucket.SeaweedFSId))
                    .FirstOrDefaultAsync(token);
                if (service is not null)
                    await ChangedAsync(tx.Session, service, "BucketPurged", bucket.Id, token);
                return tx.Commit(new SeaweedFSRetentionProcessed());
            },
            ct);
    }

    private async Task FinalizeServiceAsync(
        string tenant,
        string id,
        CancellationToken ct)
    {
        var candidate = await services.Find(SeaweedFSFilters.ById(tenant, id)).FirstOrDefaultAsync(ct);
        if (candidate?.Lifecycle is not PurgingSeaweedFSLifecycle)
            return;
        var placement = await loadPlacement(new(tenant, SeaweedFSResourceDeployment.ResourceType, id), ct);
        if (placement is null)
            return;
        var physicalDeletionConfirmed = await transactions.InvokeOutcomeAsync<bool>(
            async (tx, token) =>
            {
                var current = await services.Find(tx.Session, SeaweedFSFilters.ById(tenant, id))
                    .FirstOrDefaultAsync(token);
                if (current?.Lifecycle is not PurgingSeaweedFSLifecycle)
                    return tx.Abort(false);
                if (await fence(new(tenant, SeaweedFSResourceDeployment.ResourceType, id), tx.Session, token) is null)
                    return tx.Abort(false);
                var unfinished = SeaweedFSFilters.UnfinishedBuckets(tenant, id);
                if (await buckets.Find(tx.Session, unfinished).AnyAsync(token))
                    return tx.Commit(false);
                var state = await readiness(
                    new(tenant, SeaweedFSResourceDeployment.ResourceType, id),
                    tx.Session,
                    token);
                if (state == ResourceDeploymentFinalizationReadiness.Ready)
                    return tx.Commit(true);
                if (placement.DesiredOperation != ResourceDeploymentDesiredOperation.Delete)
                {
                    var requested = await deleteDeployment(
                        new(
                            tenant,
                            SeaweedFSResourceDeployment.ResourceType,
                            id,
                            placement.DeploymentId,
                            placement.DeploymentRevision,
                            placement.Compute.Profile,
                            ResourceRevision: current.Revision),
                        tx.Session,
                        token);
                    if (requested is ResourceDeploymentMutationError)
                        return tx.Abort(false);
                }

                return tx.Commit(false);
            },
            ct);
        if (!physicalDeletionConfirmed)
            return;
        // Filers and their operator resources are gone before dropping external metadata.
        if (candidate.Spec.Metadata is ExistingPostgresSeaweedFSMetadata)
        {
            var purgedMetadata = await purgeMetadata(new(tenant, new("pole/seaweedfs", id)), ct);
            if (purgedMetadata is not PostgresManagedDatabasePurged)
                return;
        }

        await transactions.InvokeOutcomeAsync<SeaweedFSRetentionOutcome>(
            async (tx, token) =>
            {
                var service = await services.Find(tx.Session, SeaweedFSFilters.ById(tenant, id))
                    .FirstOrDefaultAsync(token);
                if (service?.Lifecycle is not PurgingSeaweedFSLifecycle)
                    return tx.Commit(new SeaweedFSRetentionProcessed());
                var remaining = SeaweedFSFilters.UnfinishedBuckets(tenant, id);
                if (await buckets.Find(tx.Session, remaining).AnyAsync(token))
                    return tx.Commit(new SeaweedFSRetentionProcessed());
                if (placement is null)
                    return tx.Abort(new SeaweedFSRetentionConflict());
                var ready = await readiness(
                    new(tenant, SeaweedFSResourceDeployment.ResourceType, id),
                    tx.Session,
                    token);
                if (ready != ResourceDeploymentFinalizationReadiness.Ready)
                    return tx.Abort(new SeaweedFSRetentionConflict());
                if (await fence(new(tenant, SeaweedFSResourceDeployment.ResourceType, id), tx.Session, token) is null)
                    return tx.Abort(new SeaweedFSRetentionConflict());
                var tombstone = service with
                {
                    Revision = checked(service.Revision + 1),
                    Lifecycle = new DeletedSeaweedFSLifecycle(clock.GetUtcNow().UtcDateTime), ReservesName = false
                };
                if ((await services.ReplaceOneAsync(
                        tx.Session,
                        SeaweedFSFilters.AtRevision(service),
                        tombstone,
                        cancellationToken: token)).ModifiedCount != 1)
                    return tx.Abort(new SeaweedFSRetentionConflict());
                var released = await releaseSecret(new(tenant, SeaweedFSCredentials.AdminOwner(id)), tx.Session, token);
                if (released is not (ManagedSecretMutationSucceeded or ManagedSecretUnavailable))
                    return tx.Abort(new SeaweedFSRetentionConflict());
                var unregister = await registrar.UnregisterAsync(
                    new(tenant, service.ResourceGroupId, SeaweedFSResourceDeployment.ResourceType, id),
                    tx.Session,
                    token);
                if (unregister is ResourceRegistrationMutationError)
                    return tx.Abort(new SeaweedFSRetentionConflict());
                var purged = await purgeDeployments(
                    new(tenant, SeaweedFSResourceDeployment.ResourceType, id),
                    tx.Session,
                    token);
                if (purged is ResourceDeploymentFinalizationConflict)
                    return tx.Abort(new SeaweedFSRetentionConflict());
                var charge = service.QuotaCharge;
                await quotaRelease.ReleaseAsync(
                    new ReleaseQuotaUsageRequest<SeaweedFSQuotaInput>(
                        tenant,
                        new(charge.CpuMillis, charge.MemoryMi, charge.Instances, charge.StorageGb)),
                    tx.Session,
                    token);
                await ChangedAsync(tx.Session, tombstone, "Purged", null, token);
                return tx.Commit(new SeaweedFSRetentionProcessed());
            },
            ct);
    }

    private Task ChangedAsync(
        IClientSessionHandle session,
        SeaweedFSService service,
        string operation,
        string? bucketId,
        CancellationToken ct) => outbox(
        session,
        [
            OutboxWrite.Create(
                new SeaweedFSServiceChangedEvent(
                    service.Id,
                    service.Name,
                    service.ResourceGroupId,
                    service.Revision,
                    operation,
                    EventCore.Create(service.TenantId),
                    bucketId))
        ],
        ct);
}
