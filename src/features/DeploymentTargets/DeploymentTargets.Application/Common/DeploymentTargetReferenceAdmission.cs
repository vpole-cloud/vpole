using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed class DeploymentTargetReferenceAdmission(
    IDeploymentTargetDbContext dbContext,
    TimeProvider timeProvider)
    : IDeploymentTargetReferenceAdmission,
      IDeploymentTargetWorkloadConfigurationFence
{
    public Task<Result> RegisterAsync(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ApplyChangesAsync(
            owner,
            [deploymentTargetId],
            [],
            session,
            cancellationToken);

    public Task<Result> UnregisterAsync(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ApplyChangesAsync(
            owner,
            [],
            [deploymentTargetId],
            session,
            cancellationToken);

    public async Task<Result> ApplyChangesAsync(
        DeploymentTargetReferenceOwner owner,
        IReadOnlyCollection<string> addedDeploymentTargetIds,
        IReadOnlyCollection<string> removedDeploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(addedDeploymentTargetIds);
        ArgumentNullException.ThrowIfNull(removedDeploymentTargetIds);
        var ownerValidation = ValidateOwner(owner);
        if (ownerValidation is not null)
            return ownerValidation;

        var addedTargetIds = addedDeploymentTargetIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var removedTargetIds = removedDeploymentTargetIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (addedTargetIds.Any(id => !ObjectId.TryParse(id, out _)) ||
            removedTargetIds.Any(id => !ObjectId.TryParse(id, out _)))
        {
            return InvalidTargetId();
        }
        if (addedTargetIds.Intersect(removedTargetIds, StringComparer.Ordinal).Any())
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["DeploymentTargetIds"] =
                    ["A deployment target cannot be added and removed in the same change."]
            });
        }

        var addedFence = await AdvanceFencesAsync(
            addedTargetIds,
            session,
            requireAdmission: true,
            cancellationToken);
        if (addedFence is not ISuccess)
            return (Failure)addedFence;

        if (addedTargetIds.Length > 0)
        {
            var writes = new List<WriteModel<DeploymentTargetReference>>(
                addedTargetIds.Length);
            foreach (var deploymentTargetId in addedTargetIds)
            {
                var referenceResult = CreateReference(deploymentTargetId, owner);
                if (!referenceResult.TryGetValue(out var reference, out var validationFailure))
                    return validationFailure;

                writes.Add(new UpdateOneModel<DeploymentTargetReference>(
                    ReferenceFilter(reference.DeploymentTargetId, owner),
                    Builders<DeploymentTargetReference>.Update
                        .SetOnInsert(item => item.Id, reference.Id)
                        .SetOnInsert(item => item.DeploymentTargetId, reference.DeploymentTargetId)
                        .SetOnInsert(item => item.OwnerType, reference.OwnerType)
                        .SetOnInsert(item => item.OwnerId, reference.OwnerId)
                        .SetOnInsert(item => item.CreatedAtUtc, reference.CreatedAtUtc))
                {
                    IsUpsert = true
                });
            }

            await dbContext.DeploymentTargetReferences.BulkWriteAsync(
                session,
                writes,
                cancellationToken: cancellationToken);
        }

        var normalizedOwnerType = owner.Type.Trim();
        var normalizedOwnerId = owner.Id.Trim();
        var removalFilter = Builders<DeploymentTargetReference>.Filter.And(
            Builders<DeploymentTargetReference>.Filter.In(
                item => item.DeploymentTargetId,
                removedTargetIds),
            Builders<DeploymentTargetReference>.Filter.Eq(
                item => item.OwnerType,
                normalizedOwnerType),
            Builders<DeploymentTargetReference>.Filter.Eq(item => item.OwnerId, normalizedOwnerId));
        var referencedTargetIds = removedTargetIds.Length == 0
            ? []
            : await dbContext.DeploymentTargetReferences
                .Find(session, removalFilter)
                .Project(item => item.DeploymentTargetId)
                .ToListAsync(cancellationToken);
        if (referencedTargetIds.Count == 0)
            return Result.Success();

        var removedFence = await AdvanceFencesAsync(
            referencedTargetIds,
            session,
            requireAdmission: false,
            cancellationToken);
        if (removedFence is not ISuccess)
            return (Failure)removedFence;

        var deleted = await dbContext.DeploymentTargetReferences.DeleteManyAsync(
            session,
            removalFilter,
            cancellationToken: cancellationToken);
        return deleted.DeletedCount == referencedTargetIds.Count
            ? Result.Success()
            : Failures.Conflict(
                "Deployment-target references changed while removal was being accepted.");
    }

    public async Task<Result> FenceAsync(
        string deploymentTargetId,
        long expectedWorkloadConfigurationRevision,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        if (!ObjectId.TryParse(deploymentTargetId, out _) ||
            expectedWorkloadConfigurationRevision < 1)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["DeploymentTargetId"] =
                [
                    "A valid deployment target and workload configuration revision are required."
                ]
            });
        }

        var update = await dbContext.DeploymentTargets.UpdateOneAsync(
            session,
            Builders<DeploymentTarget>.Filter.And(
                Builders<DeploymentTarget>.Filter.Eq(
                    target => target.Id,
                    deploymentTargetId),
                Builders<DeploymentTarget>.Filter.Eq(
                    target => target.Enabled,
                    true),
                Builders<DeploymentTarget>.Filter.Eq(
                    target => target.AcceptsWorkloads,
                    true),
                Builders<DeploymentTarget>.Filter.Eq(
                    target => target.DeletionRequestedAtUtc,
                    null),
                Builders<DeploymentTarget>.Filter.Eq(
                    target => target.WorkloadConfiguration.Revision,
                    expectedWorkloadConfigurationRevision),
                Builders<DeploymentTarget>.Filter.Lt(
                    target => target.ReferenceFence,
                    long.MaxValue)),
            Builders<DeploymentTarget>.Update.Inc(
                target => target.ReferenceFence,
                1),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == 1
            ? Result.Success()
            : Failures.Conflict(
                "The deployment target workload configuration changed or no longer accepts workloads.");
    }

    private Result<DeploymentTargetReference> CreateReference(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner)
    {
        var ownerValidation = ValidateOwner(owner);
        if (ownerValidation is not null)
            return ownerValidation;
        if (!ObjectId.TryParse(deploymentTargetId, out _))
            return InvalidTargetId();

        return DeploymentTargetReferenceFactory.Create(
            deploymentTargetId,
            owner.Type,
            owner.Id,
            new DeploymentTargetReferenceFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));
    }

    private async Task<Result> AdvanceFencesAsync(
        IReadOnlyCollection<string> deploymentTargetIds,
        IClientSessionHandle session,
        bool requireAdmission,
        CancellationToken cancellationToken)
    {
        if (deploymentTargetIds.Count == 0)
            return Result.Success();

        var filter = Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.In(
                target => target.Id,
                deploymentTargetIds),
            Builders<DeploymentTarget>.Filter.Lt(
                target => target.ReferenceFence,
                long.MaxValue));
        if (requireAdmission)
        {
            filter &= Builders<DeploymentTarget>.Filter.And(
                Builders<DeploymentTarget>.Filter.Eq(target => target.Enabled, true),
                Builders<DeploymentTarget>.Filter.Eq(target => target.AcceptsWorkloads, true),
                Builders<DeploymentTarget>.Filter.Eq(
                    target => target.DeletionRequestedAtUtc,
                    null));
        }

        var update = await dbContext.DeploymentTargets.UpdateManyAsync(
            session,
            filter,
            Builders<DeploymentTarget>.Update.Inc(target => target.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        if (update.ModifiedCount == deploymentTargetIds.Count)
            return Result.Success();

        return requireAdmission
            ? Failures.Conflict("A deployment target no longer accepts durable references.")
            : Failures.Conflict("A deployment target is unavailable for reference removal.");
    }

    private static FilterDefinition<DeploymentTargetReference> ReferenceFilter(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner) =>
        Builders<DeploymentTargetReference>.Filter.And(
            Builders<DeploymentTargetReference>.Filter.Eq(
                item => item.DeploymentTargetId,
                deploymentTargetId),
            Builders<DeploymentTargetReference>.Filter.Eq(
                item => item.OwnerType,
                owner.Type.Trim()),
            Builders<DeploymentTargetReference>.Filter.Eq(
                item => item.OwnerId,
                owner.Id.Trim()));

    private static ValidationFailure? ValidateOwner(DeploymentTargetReferenceOwner? owner)
    {
        if (owner is null ||
            string.IsNullOrWhiteSpace(owner.Type) ||
            owner.Type.Trim().Length > DeploymentTargetReferenceFactory.MaximumOwnerTypeLength ||
            string.IsNullOrWhiteSpace(owner.Id) ||
            owner.Id.Trim().Length > DeploymentTargetReferenceFactory.MaximumOwnerIdLength)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Owner"] = ["A valid deployment-target reference owner is required."]
            });
        }

        return null;
    }

    private static ValidationFailure InvalidTargetId() =>
        Failures.ValidationFailure(new Dictionary<string, string[]>
        {
            ["DeploymentTargetId"] = ["Deployment target ID must be a MongoDB ObjectId."]
        });

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Deployment-target reference admission must run inside the reference owner's MongoDB transaction.");
        }
    }
}
