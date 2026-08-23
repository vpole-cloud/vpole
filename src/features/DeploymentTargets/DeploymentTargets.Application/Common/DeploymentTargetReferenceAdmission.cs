using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

internal sealed class DeploymentTargetReferenceAdmission(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    IMongoCollection<DeploymentTargetReference> deploymentTargetReferences,
    TimeProvider timeProvider)
{
    public async Task<Result<Unit, DeploymentTargetReferenceChangeRejected>>
        ApplyChangesOutcomeAsync(
            DeploymentTargetReferenceOwner owner,
            IReadOnlyCollection<string> addedDeploymentTargetIds,
            IReadOnlyCollection<string> removedDeploymentTargetIds,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        EnsureOutcomeArguments(
            owner,
            addedDeploymentTargetIds,
            removedDeploymentTargetIds);
        return await ApplyChangesAsync(
            owner,
            addedDeploymentTargetIds,
            removedDeploymentTargetIds,
            session,
            cancellationToken);
    }

    private static void EnsureOutcomeArguments(
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

        if (addedDeploymentTargetIds
            .Intersect(removedDeploymentTargetIds, StringComparer.Ordinal)
            .Any())
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

    private async Task<Result<Unit, DeploymentTargetReferenceChangeRejected>> ApplyChangesAsync(
        DeploymentTargetReferenceOwner owner,
        IReadOnlyCollection<string> addedDeploymentTargetIds,
        IReadOnlyCollection<string> removedDeploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var addedTargetIds = addedDeploymentTargetIds
            .ToArray();
        var removedTargetIds = removedDeploymentTargetIds
            .ToArray();

        var addedFence = await AdvanceFencesAsync(
            addedTargetIds,
            session,
            requireAdmission: true,
            cancellationToken);
        if (!addedFence)
            return Rejected(owner, addedTargetIds, removedTargetIds);

        if (addedTargetIds.Length > 0)
        {
            var writes = new List<WriteModel<DeploymentTargetReference>>(
                addedTargetIds.Length);
            foreach (var deploymentTargetId in addedTargetIds)
            {
                var reference = CreateReference(deploymentTargetId, owner);

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

            await deploymentTargetReferences.BulkWriteAsync(
                session,
                writes,
                cancellationToken: cancellationToken);
        }

        var removalFilter = DeploymentTargetReferenceFilters
            .OwnedReferencesForTargets(removedTargetIds, owner);
        var referencedTargetIds = removedTargetIds.Length == 0
            ? []
            : await deploymentTargetReferences
                .Find(session, removalFilter)
                .Project(item => item.DeploymentTargetId)
                .ToListAsync(cancellationToken);
        if (referencedTargetIds.Count == 0)
            return new Ok<Unit>(Unit.Value);

        var removedFence = await AdvanceFencesAsync(
            referencedTargetIds,
            session,
            requireAdmission: false,
            cancellationToken);
        if (!removedFence)
            return Rejected(owner, addedTargetIds, removedTargetIds);

        var deleted = await deploymentTargetReferences.DeleteManyAsync(
            session,
            removalFilter,
            cancellationToken: cancellationToken);
        return deleted.DeletedCount == referencedTargetIds.Count
            ? new Ok<Unit>(Unit.Value)
            : Rejected(owner, addedTargetIds, removedTargetIds);
    }

    public async Task<Result<
        Unit,
        DeploymentTargetWorkloadConfigurationFenceRejected>> FenceManyOutcomeAsync(
        IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        EnsureTransaction(session);
        if (requests.Count == 0)
            return new Ok<Unit>(Unit.Value);

        var distinctRequests = requests
            .DistinctBy(request => request.DeploymentTargetId, StringComparer.Ordinal)
            .ToArray();
        if (distinctRequests.Length != requests.Count ||
            distinctRequests.Any(request =>
                !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
                request.ExpectedWorkloadConfigurationRevision < 1))
        {
            throw new ArgumentException(
                "Unique valid deployment targets and workload configuration revisions are required.",
                nameof(requests));
        }

        var update = await deploymentTargets.UpdateManyAsync(
            session,
            DeploymentTargetFilters.FenceableWorkloadConfigurations(
                distinctRequests),
            Builders<DeploymentTarget>.Update.Inc(
                target => target.ReferenceFence,
                1),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == distinctRequests.Length
            ? new Ok<Unit>(Unit.Value)
            : new Err<DeploymentTargetWorkloadConfigurationFenceRejected>(
                new DeploymentTargetWorkloadConfigurationFenceRejected(requests));
    }

    private DeploymentTargetReference CreateReference(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner)
    {
        return DeploymentTargetReferenceFactory.Create(
            deploymentTargetId,
            owner.Type,
            owner.Id,
            new DeploymentTargetReferenceFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));
    }

    private async Task<bool> AdvanceFencesAsync(
        IReadOnlyCollection<string> deploymentTargetIds,
        IClientSessionHandle session,
        bool requireAdmission,
        CancellationToken cancellationToken)
    {
        if (deploymentTargetIds.Count == 0)
            return true;

        var update = await deploymentTargets.UpdateManyAsync(
            session,
            DeploymentTargetFilters.FenceableReferences(
                deploymentTargetIds,
                requireAdmission),
            Builders<DeploymentTarget>.Update.Inc(target => target.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == deploymentTargetIds.Count;
    }

    private static FilterDefinition<DeploymentTargetReference> ReferenceFilter(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner) =>
        DeploymentTargetReferenceFilters.ByOwner(deploymentTargetId, owner);

    private static Err<DeploymentTargetReferenceChangeRejected> Rejected(
        DeploymentTargetReferenceOwner owner,
        IReadOnlyCollection<string> addedDeploymentTargetIds,
        IReadOnlyCollection<string> removedDeploymentTargetIds) =>
        new(new DeploymentTargetReferenceChangeRejected(
            owner,
            addedDeploymentTargetIds
                .Concat(removedDeploymentTargetIds)
                .Order(StringComparer.Ordinal)
                .ToArray()));

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
