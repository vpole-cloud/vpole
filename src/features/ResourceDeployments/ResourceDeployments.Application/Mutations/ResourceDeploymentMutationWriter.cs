using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Mutations;

public sealed class ResourceDeploymentMutationWriter(
    IResourceDeploymentDbContext dbContext,
    IResourcePlacementLookup placementLookup,
    IComputeProfileResolutionFence computeProfileFence,
    TimeProvider timeProvider)
    : IResourceDeploymentMutationWriter,
        IResourceDeploymentSetMutationWriter,
        IResourceDeploymentSetOutcomeMutationWriter,
        IResourceDeploymentExactRetryWriter
{
    public async Task<Result<PlacementSummaryDto>> RequestApplyAsync(
        ResourceDeploymentMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);

        var loadResult = await LoadSingleAsync(command, session, cancellationToken);
        if (!loadResult.TryGetValue(out var current, out var loadFailure))
            return loadFailure;
        var computeExpectation = EnsureExpectedCompute(command, current);
        if (computeExpectation is not null)
            return computeExpectation;
        if (current.DesiredOperation == ResourceDeploymentOperation.Delete ||
            current.State is DeletedResourceDeploymentState)
        {
            return Failures.Conflict(
                "A resource deployment pending deletion cannot be reconciled.");
        }

        var runtimeCell = await placementLookup.FindAsync(
            command.TenantId,
            current.RuntimeCellId,
            session,
            cancellationToken);
        if (runtimeCell is null ||
            !string.Equals(
                runtimeCell.DeploymentTargetId,
                current.ResolvedPlacement.DeploymentTargetId,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtimeCell.KubernetesNamespace,
                current.ResolvedPlacement.KubernetesNamespace,
                StringComparison.Ordinal))
        {
            return Failures.Conflict(
                "The resource deployment runtime-cell placement is unavailable.");
        }

        var resolvedPlacement = current.ResolvedPlacement;
        if (command.ReplacementCompute is not null)
        {
            if (!string.Equals(
                    command.ReplacementCompute.DeploymentTargetId,
                    current.ResolvedPlacement.DeploymentTargetId,
                    StringComparison.Ordinal))
            {
                return Failures.Conflict(
                    "The replacement compute profile belongs to a different deployment target.");
            }

            var computeFence = await computeProfileFence.FenceAsync(
                new ComputeProfileResolutionFenceRequest(
                    command.ReplacementCompute.ComputeProfileId,
                    command.ReplacementCompute.ComputeProfileRevision,
                    command.ReplacementCompute.DeploymentTargetId,
                    command.TenantId,
                    command.ResourceType),
                session,
                cancellationToken);
            if (computeFence is not ISuccess)
                return (Failure)computeFence;

            resolvedPlacement = resolvedPlacement with
            {
                Compute = command.ReplacementCompute.ToDomain()
            };
        }

        var updated = current.RequestReconciliation(
            resolvedPlacement,
            runtimeCell.State == ResourcePlacementState.Ready,
            timeProvider.GetUtcNow().UtcDateTime);
        return await ReplaceAndEnqueueAsync(
            command,
            current,
            updated,
            session,
            cancellationToken);
    }

    public async Task<Result<PlacementSummaryDto>> RequestDeletionAsync(
        ResourceDeploymentMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);

        var loadResult = await LoadSingleAsync(command, session, cancellationToken);
        if (!loadResult.TryGetValue(out var current, out var loadFailure))
            return loadFailure;
        var computeExpectation = EnsureExpectedCompute(command, current);
        if (computeExpectation is not null)
            return computeExpectation;
        if (command.ReplacementCompute is not null)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["ComputeProfileId"] =
                    ["Compute profile adoption is not valid for resource deletion."]
            });
        }

        var updated = current.RequestDeletion(
            timeProvider.GetUtcNow().UtcDateTime);
        if (updated.Revision == current.Revision)
            return current.ToPlacementSummaryDto();

        return await ReplaceAndEnqueueAsync(
            command,
            current,
            updated,
            session,
            cancellationToken);
    }

    public async Task<Result<PlacementSummaryDto>> RequestExactApplyAsync(
        ResourceDeploymentExactMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var loadResult = await LoadExactAsync(command, session, cancellationToken);
        if (!loadResult.TryGetValue(out var current, out var loadFailure))
            return loadFailure;
        return await RequestLoadedApplyAsync(
            current,
            command,
            session,
            cancellationToken);
    }

    private async Task<Result<PlacementSummaryDto>> RequestLoadedApplyAsync(
        ResourceDeployment current,
        ResourceDeploymentExactMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (current.Revision != command.ExpectedDeploymentRevision)
        {
            return Failures.Conflict(
                "The resource deployment changed while the exact mutation was being prepared.");
        }

        var computeExpectation = EnsureExpectedCompute(command.ExpectedCompute, current);
        if (computeExpectation is not null)
            return computeExpectation;
        if (current.DesiredOperation == ResourceDeploymentOperation.Delete ||
            current.State is DeletedResourceDeploymentState)
        {
            return Failures.Conflict(
                "A resource deployment pending deletion cannot be reconciled.");
        }

        var runtimeCell = await placementLookup.FindAsync(
            command.TenantId,
            current.RuntimeCellId,
            session,
            cancellationToken);
        if (runtimeCell is null ||
            !string.Equals(
                runtimeCell.DeploymentTargetId,
                current.ResolvedPlacement.DeploymentTargetId,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtimeCell.KubernetesNamespace,
                current.ResolvedPlacement.KubernetesNamespace,
                StringComparison.Ordinal))
        {
            return Failures.Conflict(
                "The resource deployment runtime-cell placement is unavailable.");
        }

        var placement = current.ResolvedPlacement;
        if (command.ReplacementCompute is not null)
        {
            if (!string.Equals(
                    command.ReplacementCompute.DeploymentTargetId,
                    placement.DeploymentTargetId,
                    StringComparison.Ordinal))
            {
                return Failures.Conflict(
                    "The replacement compute profile belongs to a different deployment target.");
            }

            var computeFence = await computeProfileFence.FenceAsync(
                new ComputeProfileResolutionFenceRequest(
                    command.ReplacementCompute.ComputeProfileId,
                    command.ReplacementCompute.ComputeProfileRevision,
                    command.ReplacementCompute.DeploymentTargetId,
                    command.TenantId,
                    command.ResourceType),
                session,
                cancellationToken);
            if (computeFence is not ISuccess)
                return (Failure)computeFence;
            placement = placement with
            {
                Compute = command.ReplacementCompute.ToDomain()
            };
        }

        if (command.ReplacementIngress is not null)
        {
            if (!HasSameIngressIdentity(
                    current.ResolvedPlacement.Ingress,
                    command.ReplacementIngress))
            {
                return Failures.Conflict(
                    "The resolved ingress identity changed while the resource deployment mutation was being prepared.");
            }

            placement = placement with { Ingress = command.ReplacementIngress.ToDomain() };
        }

        var updated = current.RequestReconciliation(
            placement,
            runtimeCell.State == ResourcePlacementState.Ready,
            timeProvider.GetUtcNow().UtcDateTime,
            command.ResourceRevision,
            command.FixedReplicas);
        return await ReplaceExactAndEnqueueAsync(
            current,
            updated,
            session,
            cancellationToken);
    }

    public async Task<Result<PlacementSummaryDto>> RequestExactDeletionAsync(
        ResourceDeploymentExactMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var loadResult = await LoadExactAsync(command, session, cancellationToken);
        if (!loadResult.TryGetValue(out var current, out var loadFailure))
            return loadFailure;
        if (current.Revision != command.ExpectedDeploymentRevision)
        {
            return Failures.Conflict(
                "The resource deployment changed while the exact deletion was being prepared.");
        }

        var computeExpectation = EnsureExpectedCompute(command.ExpectedCompute, current);
        if (computeExpectation is not null)
            return computeExpectation;
        if (command.ReplacementCompute is not null ||
            command.ReplacementIngress is not null ||
            command.FixedReplicas is not null)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Mutation"] =
                ["Compute, ingress, and replica updates are not valid for resource deployment deletion."]
            });
        }

        var updated = current.RequestDeletion(
            timeProvider.GetUtcNow().UtcDateTime,
            command.ResourceRevision);
        if (updated.Revision == current.Revision)
            return current.ToPlacementSummaryDto();
        return await ReplaceExactAndEnqueueAsync(
            current,
            updated,
            session,
            cancellationToken);
    }

    public async Task<Result<PlacementSummaryDto>> RequestRetryAsync(
        ResourceDeploymentExactRetryRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);
        var current = await dbContext.ResourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForExactDeployment(
                    command.TenantId,
                    command.ResourceType,
                    command.ResourceId,
                    command.ResourceDeploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return Failures.NotFound(
                "Resource deployment not found.",
                command.ResourceDeploymentId);
        }
        if (current.Revision != command.ExpectedDeploymentRevision)
        {
            return Failures.Conflict(
                "The resource deployment changed while exact retry was being prepared.");
        }
        if (current.State is not FailedResourceDeploymentState
            {
                Retryable: true
            } failed ||
            !Matches(command.ExpectedOperation, failed.Operation))
        {
            return Failures.Conflict(
                "Only the expected retryable failed resource deployment operation can be retried.");
        }

        var retried = current.Retry(timeProvider.GetUtcNow().UtcDateTime);
        var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
            session,
            Builders<ResourceDeployment>.Filter.And(
                ResourceDeploymentFilters.AtRevision(current),
                Builders<ResourceDeployment>.Filter
                    .OfType<ResourceDeploymentState, FailedResourceDeploymentState>(
                        deployment => deployment.State)),
            retried,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return Failures.Conflict(
                "The resource deployment changed while exact retry was being requested.");
        }

        await dbContext.StoreEventAsync(
            retried.ToCurrentRequestWrite(),
            session,
            cancellationToken);
        return retried.ToPlacementSummaryDto();
    }

    public async Task<Result<IReadOnlyList<PlacementSummaryDto>>> AdvanceResourceRevisionAsync(
        ResourceDeploymentResourceRevisionRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var result = await AdvanceResourceRevisionOutcomeAsync(
            command,
            session,
            cancellationToken);
        return result switch
        {
            Ok<IReadOnlyList<PlacementSummaryDto>> success =>
                new Success<IReadOnlyList<PlacementSummaryDto>>(success.Value),
            Err<AdvanceResourceDeploymentRevisionError> error =>
                ToLegacyFailure(error.Value)
        };
    }

    public async Task<Result<
        IReadOnlyList<PlacementSummaryDto>,
        AdvanceResourceDeploymentRevisionError>> AdvanceResourceRevisionOutcomeAsync(
        ResourceDeploymentResourceRevisionRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var current = await dbContext.ResourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForResource(
                    command.TenantId,
                    command.ResourceType,
                    command.ResourceId))
            .SortBy(deployment => deployment.Id)
            .ToListAsync(cancellationToken);
        if (current.Count == 0)
        {
            return new Err<AdvanceResourceDeploymentRevisionError>(
                new AdvanceResourceDeploymentRevisionError(
                    new ResourceDeploymentsNotFound(command.ResourceId)));
        }

        var active = current
            .Where(deployment => deployment.State is not DeletedResourceDeploymentState)
            .ToArray();
        if (active.Length == 0)
        {
            return new Err<AdvanceResourceDeploymentRevisionError>(
                new AdvanceResourceDeploymentRevisionError(
                    new ResourceDeploymentsHaveNoActivePlacement(
                        command.ResourceId)));
        }

        var updatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var results = new List<PlacementSummaryDto>(active.Length);
        foreach (var deployment in active)
        {
            var updated = deployment.AdvanceResourceRevision(
                command.ResourceRevision,
                updatedAtUtc);
            if (updated.Revision != deployment.Revision)
            {
                var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
                    session,
                    ResourceDeploymentFilters.AtRevision(deployment),
                    updated,
                    cancellationToken: cancellationToken);
                if (replacement.ModifiedCount != 1)
                {
                    return new Err<AdvanceResourceDeploymentRevisionError>(
                        new AdvanceResourceDeploymentRevisionError(
                            new ResourceDeploymentRevisionConflict(
                                command.ResourceId)));
                }
            }

            results.Add(updated.ToPlacementSummaryDto());
        }

        return new Ok<IReadOnlyList<PlacementSummaryDto>>(results);
    }

    private static Failure ToLegacyFailure(
        AdvanceResourceDeploymentRevisionError error) => error switch
        {
            ResourceDeploymentsNotFound notFound => Failures.NotFound(
                "Resource deployments were not found.",
                notFound.ResourceId),
            ResourceDeploymentsHaveNoActivePlacement => Failures.Conflict(
                "The logical resource has no active resource deployments to advance."),
            ResourceDeploymentRevisionConflict => Failures.Conflict(
                "A resource deployment changed while the logical resource revision was being advanced.")
        };

    public async Task<Result<IReadOnlyList<PlacementSummaryDto>>> RequestSetApplyAsync(
        ResourceDeploymentSetApplyRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Deployments.Count == 0)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Deployments"] = ["At least one resource deployment is required."]
            });
        }

        var deploymentIds = command.Deployments
            .Select(item => item.ResourceDeploymentId)
            .ToArray();
        if (deploymentIds.Distinct(StringComparer.Ordinal).Count() != deploymentIds.Length)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Deployments"] = ["Resource deployment IDs must be unique."]
            });
        }

        var filter = Builders<ResourceDeployment>.Filter.And(
            ResourceDeploymentFilters.ForResource(
                command.TenantId,
                command.ResourceType,
                command.ResourceId),
            Builders<ResourceDeployment>.Filter.In(
                deployment => deployment.Id,
                deploymentIds));
        var current = await dbContext.ResourceDeployments
            .Find(session, filter)
            .ToListAsync(cancellationToken);
        if (current.Count != command.Deployments.Count)
        {
            return Failures.NotFound(
                "One or more resource deployments were not found.",
                command.ResourceId);
        }

        var byId = current.ToDictionary(deployment => deployment.Id, StringComparer.Ordinal);
        foreach (var item in command.Deployments)
        {
            var deployment = byId[item.ResourceDeploymentId];
            if (deployment.Revision != item.ExpectedDeploymentRevision)
            {
                return Failures.Conflict(
                    "A resource deployment changed while the set mutation was being prepared.");
            }
            var computeExpectation = EnsureExpectedCompute(item.ExpectedCompute, deployment);
            if (computeExpectation is not null)
                return computeExpectation;
            if (deployment.DesiredOperation == ResourceDeploymentOperation.Delete ||
                deployment.State is DeletedResourceDeploymentState)
            {
                return Failures.Conflict(
                    "A resource deployment pending deletion cannot be reconciled.");
            }
            if (item.ReplacementCompute is not null && !string.Equals(
                    item.ReplacementCompute.DeploymentTargetId,
                    deployment.ResolvedPlacement.DeploymentTargetId,
                    StringComparison.Ordinal))
            {
                return Failures.Conflict(
                    "A replacement compute profile belongs to a different deployment target.");
            }
            if (item.ReplacementIngress is not null && !HasSameIngressIdentity(
                    deployment.ResolvedPlacement.Ingress,
                    item.ReplacementIngress))
            {
                return Failures.Conflict(
                    "The resolved ingress identity changed while the resource deployment set mutation was being prepared.");
            }
        }

        var runtimeCells = await placementLookup.FindManyAsync(
            command.TenantId,
            current.Select(deployment => deployment.RuntimeCellId).ToArray(),
            session,
            cancellationToken);
        var runtimeCellsById = runtimeCells.ToDictionary(
            placement => placement.RuntimeCellId,
            StringComparer.Ordinal);
        if (runtimeCellsById.Count != current
                .Select(deployment => deployment.RuntimeCellId)
                .Distinct(StringComparer.Ordinal)
                .Count())
        {
            return Failures.Conflict(
                "One or more resource deployment runtime-cell placements are unavailable.");
        }
        foreach (var deployment in current)
        {
            var runtimeCell = runtimeCellsById[deployment.RuntimeCellId];
            if (!string.Equals(
                    runtimeCell.DeploymentTargetId,
                    deployment.ResolvedPlacement.DeploymentTargetId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    runtimeCell.KubernetesNamespace,
                    deployment.ResolvedPlacement.KubernetesNamespace,
                    StringComparison.Ordinal))
            {
                return Failures.Conflict(
                    "A resource deployment runtime-cell placement is unavailable.");
            }
        }

        var computeFenceRequests = command.Deployments
            .Where(item => item.ReplacementCompute is not null)
            .Select(item => item.ReplacementCompute!)
            .Select(compute => new ComputeProfileResolutionFenceRequest(
                compute.ComputeProfileId,
                compute.ComputeProfileRevision,
                compute.DeploymentTargetId,
                command.TenantId,
                command.ResourceType))
            .Distinct()
            .ToArray();
        if (computeFenceRequests.Length > 0)
        {
            var computeFence = await computeProfileFence.FenceManyAsync(
                computeFenceRequests,
                session,
                cancellationToken);
            if (computeFence is not ISuccess)
                return (Failure)computeFence;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updatedDeployments = new List<ResourceDeployment>(command.Deployments.Count);
        foreach (var item in command.Deployments)
        {
            var deployment = byId[item.ResourceDeploymentId];
            var placement = deployment.ResolvedPlacement;
            if (item.ReplacementCompute is not null)
                placement = placement with { Compute = item.ReplacementCompute.ToDomain() };
            if (item.ReplacementIngress is not null)
                placement = placement with { Ingress = item.ReplacementIngress.ToDomain() };

            updatedDeployments.Add(deployment.RequestReconciliation(
                placement,
                runtimeCellsById[deployment.RuntimeCellId].State ==
                    ResourcePlacementState.Ready,
                now,
                command.ResourceRevision,
                item.FixedReplicas));
        }

        foreach (var updated in updatedDeployments)
        {
            var original = byId[updated.Id];
            var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
                session,
                ResourceDeploymentFilters.AtRevision(original),
                updated,
                cancellationToken: cancellationToken);
            if (replacement.ModifiedCount != 1)
            {
                return Failures.Conflict(
                    "A resource deployment changed while the set operation was being requested.");
            }
        }
        await dbContext.StoreEventAsync(
            updatedDeployments
                .Where(deployment => deployment.State is PendingResourceDeploymentState)
                .Select(deployment => deployment.ToCurrentRequestWrite()),
            session,
            cancellationToken);

        return updatedDeployments
            .Select(deployment => deployment.ToPlacementSummaryDto())
            .ToArray();
    }

    public async Task<Result<IReadOnlyList<PlacementSummaryDto>>> RequestSetDeletionAsync(
        ResourceDeploymentSetDeletionRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Deployments.Count == 0)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Deployments"] = ["At least one resource deployment is required."]
            });
        }

        var deploymentIds = command.Deployments
            .Select(item => item.ResourceDeploymentId)
            .ToArray();
        if (deploymentIds.Distinct(StringComparer.Ordinal).Count() != deploymentIds.Length)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Deployments"] = ["Resource deployment IDs must be unique."]
            });
        }

        var current = await dbContext.ResourceDeployments
            .Find(
                session,
                Builders<ResourceDeployment>.Filter.And(
                    ResourceDeploymentFilters.ForResource(
                        command.TenantId,
                        command.ResourceType,
                        command.ResourceId),
                    Builders<ResourceDeployment>.Filter.In(
                        deployment => deployment.Id,
                        deploymentIds)))
            .ToListAsync(cancellationToken);
        if (current.Count != command.Deployments.Count)
        {
            return Failures.NotFound(
                "One or more resource deployments were not found.",
                command.ResourceId);
        }

        var byId = current.ToDictionary(deployment => deployment.Id, StringComparer.Ordinal);
        foreach (var item in command.Deployments)
        {
            var deployment = byId[item.ResourceDeploymentId];
            if (deployment.Revision != item.ExpectedDeploymentRevision)
            {
                return Failures.Conflict(
                    "A resource deployment changed while set deletion was being prepared.");
            }
            var computeExpectation = EnsureExpectedCompute(item.ExpectedCompute, deployment);
            if (computeExpectation is not null)
                return computeExpectation;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updatedDeployments = command.Deployments
            .Select(item => byId[item.ResourceDeploymentId].RequestDeletion(
                now,
                command.ResourceRevision))
            .ToArray();
        var changedDeployments = updatedDeployments
            .Where(updated => updated.Revision != byId[updated.Id].Revision)
            .ToArray();
        foreach (var updated in changedDeployments)
        {
            var original = byId[updated.Id];
            var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
                session,
                ResourceDeploymentFilters.AtRevision(original),
                updated,
                cancellationToken: cancellationToken);
            if (replacement.ModifiedCount != 1)
            {
                return Failures.Conflict(
                    "A resource deployment changed while set deletion was being requested.");
            }
        }
        await dbContext.StoreEventAsync(
            changedDeployments
                .Where(deployment => deployment.State is PendingResourceDeploymentState)
                .Select(deployment => deployment.ToCurrentRequestWrite()),
            session,
            cancellationToken);

        return updatedDeployments
            .Select(deployment => deployment.ToPlacementSummaryDto())
            .ToArray();
    }

    private async Task<Result<ResourceDeployment>> LoadSingleAsync(
        ResourceDeploymentMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ResourceType.Value))
            throw new ArgumentException("Resource type is required.", nameof(command));

        var deployments = await dbContext.ResourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForResource(
                    command.TenantId,
                    command.ResourceType,
                    command.ResourceId))
            .SortBy(deployment => deployment.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        return deployments.Count switch
        {
            0 => Failures.NotFound(
                "Resource deployment not found.",
                command.ResourceId),
            1 => deployments[0],
            _ => Failures.Conflict(
                "The logical resource has multiple deployments and cannot be mutated as a single-target resource.")
        };
    }

    private async Task<Result<ResourceDeployment>> LoadExactAsync(
        ResourceDeploymentExactMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ResourceType.Value))
            throw new ArgumentException("Resource type is required.", nameof(command));

        var deployment = await dbContext.ResourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForExactDeployment(
                    command.TenantId,
                    command.ResourceType,
                    command.ResourceId,
                    command.ResourceDeploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        return deployment is null
            ? Failures.NotFound(
                "Resource deployment not found.",
                command.ResourceDeploymentId)
            : deployment;
    }

    private static ConflictFailure? EnsureExpectedCompute(
        ResourceDeploymentMutationRequest command,
        ResourceDeployment current) =>
        EnsureExpectedCompute(command.ExpectedCompute, current);

    private static ConflictFailure? EnsureExpectedCompute(
        ComputeProfileReference expectedCompute,
        ResourceDeployment current) =>
        current.ResolvedPlacement.Compute.Matches(
            expectedCompute.ComputeProfileId,
            expectedCompute.ComputeProfileRevision)
            ? null
            : Failures.Conflict(
                "The resource deployment compute configuration changed while the logical resource mutation was being prepared.");

    private static bool HasSameIngressIdentity(
        ResolvedIngressConfiguration? current,
        ResourceDeploymentIngressConfiguration replacement) =>
        current is not null &&
        string.Equals(
            current.IngressClassName,
            replacement.IngressClassName,
            StringComparison.Ordinal) &&
        string.Equals(
            current.ClusterIssuerName,
            replacement.ClusterIssuerName,
            StringComparison.Ordinal) &&
        string.Equals(
            current.PublicBaseDomain,
            replacement.PublicBaseDomain,
            StringComparison.Ordinal);

    private static bool Matches(
        ResourceDeploymentRequestedOperation expected,
        ResourceDeploymentOperation current) =>
        (expected, current) switch
        {
            (ResourceDeploymentRequestedOperation.Apply,
                ResourceDeploymentOperation.Apply) => true,
            (ResourceDeploymentRequestedOperation.Delete,
                ResourceDeploymentOperation.Delete) => true,
            _ => false
        };

    private async Task<Result<PlacementSummaryDto>> ReplaceAndEnqueueAsync(
        ResourceDeploymentMutationRequest command,
        ResourceDeployment current,
        ResourceDeployment updated,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
            session,
            ResourceDeploymentFilters.AtRevision(current),
            updated,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return Failures.Conflict(
                "The resource deployment changed while the operation was being requested.");
        }

        if (updated.State is PendingResourceDeploymentState)
        {
            await dbContext.StoreEventAsync(
                updated.ToCurrentRequestWrite(),
                session,
                cancellationToken);
        }

        return updated.ToPlacementSummaryDto();
    }

    private async Task<Result<PlacementSummaryDto>> ReplaceExactAndEnqueueAsync(
        ResourceDeployment current,
        ResourceDeployment updated,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
            session,
            ResourceDeploymentFilters.AtRevision(current),
            updated,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return Failures.Conflict(
                "The resource deployment changed while the exact operation was being requested.");
        }

        if (updated.State is PendingResourceDeploymentState)
        {
            await dbContext.StoreEventAsync(
                updated.ToCurrentRequestWrite(),
                session,
                cancellationToken);
        }

        return updated.ToPlacementSummaryDto();
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource deployment mutations must run inside the logical resource transaction.");
        }
    }
}
