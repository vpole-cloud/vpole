using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Mongo;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.SeaweedFS.Events;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.SeaweedFS.Application.Services;

public sealed record SeaweedFSNotFound;

public sealed record SeaweedFSConflict(
    string Code
);

public sealed record SeaweedFSCredentialFailure;

public sealed record SeaweedFSForbidden;

public union SeaweedFSMutationOutcome(
    SeaweedFSServiceDto,
    SeaweedFSBucketDto,
    SeaweedFSNotFound,
    SeaweedFSConflict,
    SeaweedFSCredentialFailure);

public sealed class SeaweedFSSettings
{
    public TimeSpan DeletionRetention { get; set; }
}

public sealed class SeaweedFSMutations(
    IMongoCollection<SeaweedFSService> services,
    IMongoCollection<SeaweedFSBucket> buckets,
    MongoRunTransaction transactions,
    PrepareManagedSecret prepare,
    CreateBoundManagedSecret createSecret,
    ReadManagedSecret readSecret,
    RotateManagedSecret rotateSecret,
    LoadResourceDeployment loadDeployment,
    RequestExactResourceDeploymentApply apply,
    FenceRegisteredResource fence,
    StoreOutboxWrites outbox,
    IOptions<SeaweedFSSettings> settings,
    TimeProvider clock
)
{
    public Task<SeaweedFSMutationOutcome> UpdateAsync(
        string tenant,
        string id,
        long expected,
        ImmutableHashSet<string> tags,
        CancellationToken ct) => MutateAsync(tenant, id, expected, null, null, "Updated", tags, ct);

    public Task<SeaweedFSMutationOutcome> DeleteAsync(
        string tenant,
        string id,
        long expected,
        CancellationToken ct) => MutateAsync(tenant, id, expected, null, null, "Deleted", null, ct);

    public Task<SeaweedFSMutationOutcome> RestoreAsync(
        string tenant,
        string id,
        long expected,
        CancellationToken ct) => MutateAsync(tenant, id, expected, null, null, "Restored", null, ct);

    public Task<SeaweedFSMutationOutcome> DeleteBucketAsync(
        string tenant,
        string id,
        string bucket,
        long expected,
        CancellationToken ct) => MutateAsync(tenant, id, expected, bucket, null, "BucketDeleted", null, ct);

    public Task<SeaweedFSMutationOutcome> RestoreBucketAsync(
        string tenant,
        string id,
        string bucket,
        long expected,
        CancellationToken ct) => MutateAsync(tenant, id, expected, bucket, null, "BucketRestored", null, ct);

    public async Task<SeaweedFSMutationOutcome> CreateBucketAsync(
        string tenant,
        string id,
        string name,
        CancellationToken ct)
    {
        var bucketId = ObjectId.GenerateNewId().ToString();
        var secretId = ObjectId.GenerateNewId().ToString();
        var result = await prepare(
            SeaweedFSCredentials.Prepare(
                tenant,
                secretId,
                SeaweedFSCredentials.BucketOwner(id, bucketId),
                1,
                ManagedSecretSharingPolicy.Shareable),
            ct);
        if (result is ManagedSecretEncryptionUnavailable)
            return new SeaweedFSCredentialFailure();
        var prepared = (PreparedManagedSecret)result.Value;
        return await MutateAsync(
            tenant,
            id,
            0,
            bucketId,
            new SeaweedFSBucket(bucketId, tenant, id, name, secretId, 1, new ActiveSeaweedFSLifecycle(), true),
            "BucketCreated",
            null,
            ct,
            prepared);
    }

    public async Task<SeaweedFSMutationOutcome> RotateBucketAsync(
        string tenant,
        string id,
        string bucketId,
        long expected,
        CancellationToken ct)
    {
        var read = await readSecret(tenant, SeaweedFSCredentials.BucketOwner(id, bucketId), ct);
        if (read is not ManagedSecretValues values)
            return new SeaweedFSCredentialFailure();
        var result = await prepare(
            SeaweedFSCredentials.Prepare(
                tenant,
                values.SecretId,
                SeaweedFSCredentials.BucketOwner(id, bucketId),
                checked(values.Version + 1),
                ManagedSecretSharingPolicy.Shareable),
            ct);
        if (result is ManagedSecretEncryptionUnavailable)
            return new SeaweedFSCredentialFailure();
        return await MutateAsync(
            tenant,
            id,
            expected,
            bucketId,
            null,
            "BucketCredentialsRotated",
            null,
            ct,
            (PreparedManagedSecret)result.Value,
            values.Version);
    }

    private async Task<SeaweedFSMutationOutcome> MutateAsync(
        string tenant,
        string id,
        long expected,
        string? bucketId,
        SeaweedFSBucket? created,
        string operation,
        ImmutableHashSet<string>? tags,
        CancellationToken ct,
        PreparedManagedSecret? prepared = null,
        long secretVersion = 0)
    {
        var placement = await loadDeployment(new(tenant, SeaweedFSResourceDeployment.ResourceType, id), ct);
        return await transactions.InvokeOutcomeAsync<SeaweedFSMutationOutcome>(
            async (tx, token) =>
            {
                var service = await services.Find(tx.Session, SeaweedFSFilters.ById(tenant, id))
                    .FirstOrDefaultAsync(token);
                if (service is null)
                    return tx.Abort(new SeaweedFSNotFound());
                if (placement is null)
                    return tx.Abort(new SeaweedFSConflict("placement-unavailable"));
                if (operation == "Updated" && service.Lifecycle is ActiveSeaweedFSLifecycle &&
                    service.Revision == expected && tags is not null && service.Tags.SetEquals(tags))
                    return tx.Commit(service.ToDto(placement));
                if (await fence(new(tenant, SeaweedFSResourceDeployment.ResourceType, id), tx.Session, token) is null)
                    return tx.Abort(new SeaweedFSConflict("registration-unavailable"));
                var now = clock.GetUtcNow().UtcDateTime;
                SeaweedFSBucket? bucket = created;
                if (bucketId is not null && created is null)
                {
                    bucket = await buckets.Find(tx.Session, SeaweedFSFilters.BucketById(tenant, id, bucketId!))
                        .FirstOrDefaultAsync(token);
                    if (bucket is null)
                        return tx.Abort(new SeaweedFSNotFound());
                    if (bucket.Revision != expected)
                        return tx.Abort(new SeaweedFSConflict("revision-conflict"));
                }
                else if (bucketId is null && service.Revision != expected)
                    return tx.Abort(new SeaweedFSConflict("revision-conflict"));

                if (operation == "Restored")
                {
                    if (service.Lifecycle is not DeletingSeaweedFSLifecycle deleting ||
                        now >= deleting.DeletionDeadlineUtc)
                        return tx.Abort(new SeaweedFSConflict("restore-unavailable"));
                }
                else if (service.Lifecycle is not ActiveSeaweedFSLifecycle)
                    return tx.Abort(new SeaweedFSConflict("lifecycle-conflict"));

                var replacement = operation switch
                {
                    "Deleted" => service.BeginDeletion(now, settings.Value.DeletionRetention),
                    "Restored" => service with
                    {
                        Revision = checked(service.Revision + 1), Lifecycle = new ActiveSeaweedFSLifecycle()
                    },
                    _ => service with { Revision = checked(service.Revision + 1), Tags = tags ?? service.Tags }
                };
                if (bucket is not null && created is null)
                {
                    if (operation == "BucketRestored")
                    {
                        if (bucket.Lifecycle is not DeletingSeaweedFSLifecycle deleting ||
                            now >= deleting.DeletionDeadlineUtc)
                            return tx.Abort(new SeaweedFSConflict("restore-unavailable"));
                        bucket = bucket with
                        {
                            Revision = checked(bucket.Revision + 1), Lifecycle = new ActiveSeaweedFSLifecycle()
                        };
                    }
                    else
                    {
                        if (bucket.Lifecycle is not ActiveSeaweedFSLifecycle)
                            return tx.Abort(new SeaweedFSConflict("lifecycle-conflict"));
                        bucket = bucket with
                        {
                            Revision = checked(bucket.Revision + 1),
                            Lifecycle = operation == "BucketDeleted"
                                ? new DeletingSeaweedFSLifecycle(now, now.Add(settings.Value.DeletionRetention))
                                : bucket.Lifecycle
                        };
                    }
                }

                var replaced = await services.ReplaceOneAsync(
                    tx.Session,
                    SeaweedFSFilters.AtRevision(service),
                    replacement,
                    cancellationToken: token);
                if (replaced.ModifiedCount != 1)
                    return tx.Abort(new SeaweedFSConflict("revision-conflict"));
                if (created is not null)
                {
                    try
                    {
                        await buckets.InsertOneAsync(tx.Session, created, cancellationToken: token);
                    }
                    catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey &&
                                                         ex.WriteError.Message.Contains(
                                                             "UX_SeaweedFSBuckets_Tenant_Service_Name",
                                                             StringComparison.Ordinal))
                    {
                        return tx.Abort(new SeaweedFSConflict("bucket-name-conflict"));
                    }

                    var secret = await createSecret(
                        new(prepared!, service.ResourceGroupId, placement.IsolationScopeId, placement.DeploymentId),
                        tx.Session,
                        token);
                    if (secret is not ManagedSecretMutationSucceeded)
                        return tx.Abort(new SeaweedFSCredentialFailure());
                }
                else if (bucket is not null)
                {
                    var replacedBucket = await buckets.ReplaceOneAsync(
                        tx.Session,
                        SeaweedFSFilters.BucketAtRevision(tenant, id, bucketId!, expected),
                        bucket,
                        cancellationToken: token);
                    if (replacedBucket.ModifiedCount != 1)
                        return tx.Abort(new SeaweedFSConflict("revision-conflict"));
                    if (prepared is not null)
                    {
                        var rotated = await rotateSecret(new(prepared, secretVersion), tx.Session, token);
                        if (rotated is not ManagedSecretMutationSucceeded)
                            return tx.Abort(new SeaweedFSCredentialFailure());
                    }
                }

                var applied = await apply(
                    new(
                        tenant,
                        SeaweedFSResourceDeployment.ResourceType,
                        id,
                        placement.DeploymentId,
                        placement.DeploymentRevision,
                        placement.Compute.Profile,
                        ResourceRevision: replacement.Revision),
                    tx.Session,
                    token);
                if (applied is ResourceDeploymentMutationError)
                    return tx.Abort(new SeaweedFSConflict("deployment-changed"));
                await outbox(
                    tx.Session,
                    [
                        OutboxWrite.Create(
                            new SeaweedFSServiceChangedEvent(
                                id,
                                service.Name,
                                service.ResourceGroupId,
                                replacement.Revision,
                                operation,
                                EventCore.Create(tenant),
                                bucketId))
                    ],
                    token);
                return tx.Commit(
                    bucket is null
                        ? new SeaweedFSMutationOutcome(replacement.ToDto((PlacementSummaryDto)applied.Value))
                        : new SeaweedFSMutationOutcome(bucket.ToDto()));
            },
            ct);
    }
}
