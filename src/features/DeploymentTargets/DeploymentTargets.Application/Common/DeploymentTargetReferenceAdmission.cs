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
        var result = await ApplyChangesAsync(
            owner,
            addedDeploymentTargetIds,
            removedDeploymentTargetIds,
            session,
            cancellationToken);
        return result is ISuccess
            ? new Ok<Unit>(Unit.Value)
            : new Err<DeploymentTargetReferenceChangeRejected>(
                new DeploymentTargetReferenceChangeRejected(
                    owner,
                    addedDeploymentTargetIds
                        .Concat(removedDeploymentTargetIds)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray()));
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
    }

    private async Task<Result> ApplyChangesAsync(
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
            return Result.Success();

        var removedFence = await AdvanceFencesAsync(
            referencedTargetIds,
            session,
            requireAdmission: false,
            cancellationToken);
        if (removedFence is not ISuccess)
            return (Failure)removedFence;

        var deleted = await deploymentTargetReferences.DeleteManyAsync(
            session,
            removalFilter,
            cancellationToken: cancellationToken);
        return deleted.DeletedCount == referencedTargetIds.Count
            ? Result.Success()
            : Failures.Conflict(
                "Deployment-target references changed while removal was being accepted.");
    }

    public async Task<Result<
        Unit,
        DeploymentTargetWorkloadConfigurationFenceRejected>> FenceManyOutcomeAsync(
        IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var result = await FenceManyAsync(requests, session, cancellationToken);
        return result is ISuccess
            ? new Ok<Unit>(Unit.Value)
            : new Err<DeploymentTargetWorkloadConfigurationFenceRejected>(
                new DeploymentTargetWorkloadConfigurationFenceRejected(requests));
    }

    private async Task<Result> FenceManyAsync(
        IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return Result.Success();

        var distinctRequests = requests
            .DistinctBy(request => request.DeploymentTargetId, StringComparer.Ordinal)
            .ToArray();
        if (distinctRequests.Length != requests.Count ||
            distinctRequests.Any(request =>
                !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
                request.ExpectedWorkloadConfigurationRevision < 1))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["DeploymentTargets"] =
                [
                    "Unique valid deployment targets and workload configuration revisions are required."
                ]
            });
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

        var update = await deploymentTargets.UpdateManyAsync(
            session,
            DeploymentTargetFilters.FenceableReferences(
                deploymentTargetIds,
                requireAdmission),
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
        DeploymentTargetReferenceFilters.ByOwner(deploymentTargetId, owner);

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
