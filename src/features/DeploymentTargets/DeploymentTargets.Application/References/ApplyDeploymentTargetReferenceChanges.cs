using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.References;

internal sealed class MongoApplyDeploymentTargetReferenceChanges(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    IMongoCollection<DeploymentTargetReference> deploymentTargetReferences,
    TimeProvider timeProvider)
{
    public async Task<ApplyDeploymentTargetReferenceChangesOutcome> InvokeAsync(
        ApplyDeploymentTargetReferenceChangesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureArguments(request.Owner, request.AddedDeploymentTargetIds, request.RemovedDeploymentTargetIds);
        EnsureTransaction(session);
        var addedTargetIds = request.AddedDeploymentTargetIds.ToArray();
        var removedTargetIds = request.RemovedDeploymentTargetIds.ToArray();

        var addedFence = await AdvanceFencesAsync(
            addedTargetIds,
            request.Owner,
            session,
            requireAdmission: true,
            cancellationToken);
        if (!addedFence)
            return Rejected(request.Owner, addedTargetIds, removedTargetIds);

        if (addedTargetIds.Length > 0)
        {
            var writes = new List<WriteModel<DeploymentTargetReference>>(addedTargetIds.Length);
            foreach (var deploymentTargetId in addedTargetIds)
            {
                var reference = DeploymentTargetReferenceFactory.Create(
                    deploymentTargetId,
                    request.Owner.Type,
                    request.Owner.Id,
                    new DeploymentTargetReferenceFactory.CreateOptions(
                        ObjectId.GenerateNewId().ToString(),
                        timeProvider.GetUtcNow().UtcDateTime));
                writes.Add(new UpdateOneModel<DeploymentTargetReference>(
                    DeploymentTargetReferenceFilters.ByOwner(reference.DeploymentTargetId, request.Owner),
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

            await deploymentTargetReferences.BulkWriteAsync(
                session,
                writes,
                cancellationToken: cancellationToken);
        }

        var removalFilter = DeploymentTargetReferenceFilters
            .OwnedReferencesForTargets(removedTargetIds, request.Owner);
        var referencedTargetIds = removedTargetIds.Length == 0
            ? []
            : await deploymentTargetReferences
                .Find(session, removalFilter)
                .Project(item => item.DeploymentTargetId)
                .ToListAsync(cancellationToken);
        if (referencedTargetIds.Count == 0)
            return new DeploymentTargetReferencesChanged();

        var removedFence = await AdvanceFencesAsync(
            referencedTargetIds,
            request.Owner,
            session,
            requireAdmission: false,
            cancellationToken);
        if (!removedFence)
            return Rejected(request.Owner, addedTargetIds, removedTargetIds);

        var deleted = await deploymentTargetReferences.DeleteManyAsync(
            session,
            removalFilter,
            cancellationToken: cancellationToken);
        return deleted.DeletedCount == referencedTargetIds.Count
            ? new DeploymentTargetReferencesChanged()
            : Rejected(request.Owner, addedTargetIds, removedTargetIds);
    }

    private async Task<bool> AdvanceFencesAsync(
        IReadOnlyCollection<string> deploymentTargetIds,
        DeploymentTargetReferenceOwner owner,
        IClientSessionHandle session,
        bool requireAdmission,
        CancellationToken cancellationToken)
    {
        if (deploymentTargetIds.Count == 0)
            return true;

        var update = await deploymentTargets.UpdateManyAsync(
            session,
            DeploymentTargetFilters.FenceableReferences(deploymentTargetIds, requireAdmission, owner),
            Builders<DeploymentTarget>.Update.Inc(target => target.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == deploymentTargetIds.Count;
    }

    private static ApplyDeploymentTargetReferenceChangesOutcome Rejected(
        DeploymentTargetReferenceOwner owner,
        IReadOnlyCollection<string> addedDeploymentTargetIds,
        IReadOnlyCollection<string> removedDeploymentTargetIds) => new DeploymentTargetReferenceChangeRejected(
            owner,
            addedDeploymentTargetIds
                .Concat(removedDeploymentTargetIds)
                .Order(StringComparer.Ordinal)
                .ToArray());

    private static void EnsureArguments(
        DeploymentTargetReferenceOwner owner,
        IReadOnlyCollection<string> addedDeploymentTargetIds,
        IReadOnlyCollection<string> removedDeploymentTargetIds)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(addedDeploymentTargetIds);
        ArgumentNullException.ThrowIfNull(removedDeploymentTargetIds);
        if (string.IsNullOrWhiteSpace(owner.Type) ||
            owner.Type.Trim().Length > DeploymentTargetReferenceFactory.MaximumOwnerTypeLength ||
            string.IsNullOrWhiteSpace(owner.Id) ||
            owner.Id.Trim().Length > DeploymentTargetReferenceFactory.MaximumOwnerIdLength)
        {
            throw new ArgumentException(
                "A valid deployment-target reference owner is required.",
                nameof(owner));
        }
        if (addedDeploymentTargetIds.Any(id => !ObjectId.TryParse(id, out _)) ||
            removedDeploymentTargetIds.Any(id => !ObjectId.TryParse(id, out _)))
        {
            throw new ArgumentException(
                "Deployment target IDs must be MongoDB ObjectIds.",
                nameof(addedDeploymentTargetIds));
        }
        if (addedDeploymentTargetIds.Intersect(removedDeploymentTargetIds, StringComparer.Ordinal).Any())
        {
            throw new ArgumentException(
                "A deployment target cannot be added and removed in the same change.",
                nameof(addedDeploymentTargetIds));
        }
        if (addedDeploymentTargetIds.Count != addedDeploymentTargetIds
                .Distinct(StringComparer.Ordinal)
                .Count() ||
            removedDeploymentTargetIds.Count != removedDeploymentTargetIds
                .Distinct(StringComparer.Ordinal)
                .Count())
        {
            throw new ArgumentException(
                "A deployment target can occur at most once in a reference change.",
                nameof(addedDeploymentTargetIds));
        }
    }

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
