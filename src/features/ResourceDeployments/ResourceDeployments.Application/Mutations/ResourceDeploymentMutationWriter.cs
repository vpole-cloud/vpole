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

internal sealed class ResourceDeploymentMutationWriter(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    IResourcePlacementLookup placementLookup,
    FenceResolvedComputeProfiles fenceResolvedComputeProfiles,
    TimeProvider timeProvider)
{
    public async Task<Result<PlacementSummaryDto, ResourceDeploymentMutationError>>
        RequestApplyAsync(
        ResourceDeploymentMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);

        var loadResult = await LoadSingleAsync(command, session, cancellationToken);
        if (!loadResult.TryGetValue(out var current, out var loadFailure))
            return Error(loadFailure);
        var computeExpectation = EnsureExpectedCompute(command, current);
        if (computeExpectation is not null)
            return Error(computeExpectation.Value);
        if (current.DesiredOperation == ResourceDeploymentOperation.Delete ||
            current.State is DeletedResourceDeploymentState)
        {
            return Error(new ResourceDeploymentPendingDeletion(command.ResourceId));
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
            return Error(new ResourceDeploymentPlacementUnavailable(command.ResourceId));
        }

        var resolvedPlacement = current.ResolvedPlacement;
        if (command.ReplacementCompute is not null)
        {
            if (!string.Equals(
                    command.ReplacementCompute.DeploymentTargetId,
                    current.ResolvedPlacement.DeploymentTargetId,
                    StringComparison.Ordinal))
            {
                return Error(new ResourceDeploymentReplacementTargetMismatch(
                    command.ResourceId));
            }

            var computeFence = await fenceResolvedComputeProfiles(
                [new ComputeProfileResolutionFenceRequest(
                    command.ReplacementCompute.ComputeProfileId,
                    command.ReplacementCompute.ComputeProfileRevision,
                    command.ReplacementCompute.DeploymentTargetId,
                    command.TenantId,
                    command.ResourceType)],
                session,
                cancellationToken);
            if (computeFence is Err<ComputeProfileResolutionFenceRejected>)
            {
                return Error(new ResourceDeploymentComputeConfigurationChanged(
                    [command.ReplacementCompute.ComputeProfileId]));
            }

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

    public async Task<Result<PlacementSummaryDto, ResourceDeploymentMutationError>>
        RequestDeletionAsync(
        ResourceDeploymentMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);

        var loadResult = await LoadSingleAsync(command, session, cancellationToken);
        if (!loadResult.TryGetValue(out var current, out var loadFailure))
            return Error(loadFailure);
        var computeExpectation = EnsureExpectedCompute(command, current);
        if (computeExpectation is not null)
            return Error(computeExpectation.Value);
        if (command.ReplacementCompute is not null)
        {
            throw new ArgumentException(
                "Compute profile adoption is not valid for resource deletion.",
                nameof(command));
        }

        var updated = current.RequestDeletion(
            timeProvider.GetUtcNow().UtcDateTime);
        if (updated.Revision == current.Revision)
            return Success(current.ToPlacementSummaryDto());

        return await ReplaceAndEnqueueAsync(
            command,
            current,
            updated,
            session,
            cancellationToken);
    }

    public async Task<Result<PlacementSummaryDto, ResourceDeploymentMutationError>>
        RequestExactApplyAsync(
        ResourceDeploymentExactMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);
        var loadResult = await LoadExactAsync(command, session, cancellationToken);
        if (!loadResult.TryGetValue(out var current, out var loadFailure))
            return Error(loadFailure);
        return await RequestLoadedApplyAsync(
            current,
            command,
            session,
            cancellationToken);
    }

    private async Task<Result<PlacementSummaryDto, ResourceDeploymentMutationError>>
        RequestLoadedApplyAsync(
        ResourceDeployment current,
        ResourceDeploymentExactMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (current.Revision != command.ExpectedDeploymentRevision)
        {
            return Error(new ResourceDeploymentChanged(
                command.ResourceId,
                command.ResourceDeploymentId));
        }

        var computeExpectation = EnsureExpectedCompute(
            command.ResourceId,
            command.ExpectedCompute,
            current);
        if (computeExpectation is not null)
            return Error(computeExpectation.Value);
        if (current.DesiredOperation == ResourceDeploymentOperation.Delete ||
            current.State is DeletedResourceDeploymentState)
        {
            return Error(new ResourceDeploymentPendingDeletion(
                command.ResourceId,
                command.ResourceDeploymentId));
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
            return Error(new ResourceDeploymentPlacementUnavailable(
                command.ResourceId,
                command.ResourceDeploymentId));
        }

        var placement = current.ResolvedPlacement;
        if (command.ReplacementCompute is not null)
        {
            if (!string.Equals(
                    command.ReplacementCompute.DeploymentTargetId,
                    placement.DeploymentTargetId,
                    StringComparison.Ordinal))
            {
                return Error(new ResourceDeploymentReplacementTargetMismatch(
                    command.ResourceId,
                    command.ResourceDeploymentId));
            }

            var computeFence = await fenceResolvedComputeProfiles(
                [new ComputeProfileResolutionFenceRequest(
                    command.ReplacementCompute.ComputeProfileId,
                    command.ReplacementCompute.ComputeProfileRevision,
                    command.ReplacementCompute.DeploymentTargetId,
                    command.TenantId,
                    command.ResourceType)],
                session,
                cancellationToken);
            if (computeFence is Err<ComputeProfileResolutionFenceRejected>)
            {
                return Error(new ResourceDeploymentComputeConfigurationChanged(
                    [command.ReplacementCompute.ComputeProfileId]));
            }
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
                return Error(new ResourceDeploymentIngressConfigurationChanged(
                    command.ResourceId,
                    command.ResourceDeploymentId));
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

    public async Task<Result<PlacementSummaryDto, ResourceDeploymentMutationError>>
        RequestExactDeletionAsync(
        ResourceDeploymentExactMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);
        var loadResult = await LoadExactAsync(command, session, cancellationToken);
        if (!loadResult.TryGetValue(out var current, out var loadFailure))
            return Error(loadFailure);
        if (current.Revision != command.ExpectedDeploymentRevision)
        {
            return Error(new ResourceDeploymentChanged(
                command.ResourceId,
                command.ResourceDeploymentId));
        }

        var computeExpectation = EnsureExpectedCompute(
            command.ResourceId,
            command.ExpectedCompute,
            current);
        if (computeExpectation is not null)
            return Error(computeExpectation.Value);
        if (command.ReplacementCompute is not null ||
            command.ReplacementIngress is not null ||
            command.FixedReplicas is not null)
        {
            throw new ArgumentException(
                "Compute, ingress, and replica updates are not valid for resource deployment deletion.",
                nameof(command));
        }

        var updated = current.RequestDeletion(
            timeProvider.GetUtcNow().UtcDateTime,
            command.ResourceRevision);
        if (updated.Revision == current.Revision)
            return Success(current.ToPlacementSummaryDto());
        return await ReplaceExactAndEnqueueAsync(
            current,
            updated,
            session,
            cancellationToken);
    }

    public async Task<Result<PlacementSummaryDto, ResourceDeploymentMutationError>>
        RequestRetryAsync(
        ResourceDeploymentExactRetryRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);
        var current = await resourceDeployments
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
            return Error(new ResourceDeploymentMutationNotFound(
                command.ResourceId,
                command.ResourceDeploymentId));
        }
        if (current.Revision != command.ExpectedDeploymentRevision)
        {
            return Error(new ResourceDeploymentChanged(
                command.ResourceId,
                command.ResourceDeploymentId));
        }
        if (current.State is not FailedResourceDeploymentState
            {
                Retryable: true
            } failed ||
            !Matches(command.ExpectedOperation, failed.Operation))
        {
            return Error(new ResourceDeploymentRetryUnavailable(
                command.ResourceId,
                command.ResourceDeploymentId));
        }

        var retried = current.Retry(timeProvider.GetUtcNow().UtcDateTime);
        var replacement = await resourceDeployments.ReplaceOneAsync(
            session,
            ResourceDeploymentFilters.ForRetryAtRevision(current),
            retried,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return Error(new ResourceDeploymentChanged(
                command.ResourceId,
                command.ResourceDeploymentId));
        }

        await storeOutboxWrites(
            session,
            [retried.ToCurrentRequestWrite()],
            cancellationToken);
        return Success(retried.ToPlacementSummaryDto());
    }

    public async Task<Result<
        IReadOnlyList<PlacementSummaryDto>,
        AdvanceResourceDeploymentRevisionError>> AdvanceResourceRevisionOutcomeAsync(
        ResourceDeploymentResourceRevisionRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);
        var current = await resourceDeployments
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
                var replacement = await resourceDeployments.ReplaceOneAsync(
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

    public async Task<Result<
        IReadOnlyList<PlacementSummaryDto>,
        ResourceDeploymentMutationError>> RequestSetApplyAsync(
        ResourceDeploymentSetApplyRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Deployments);
        if (command.Deployments.Any(static deployment => deployment is null))
        {
            throw new ArgumentException(
                "A deployment set cannot contain null entries.",
                nameof(command));
        }
        if (command.Deployments.Count == 0)
        {
            throw new ArgumentException(
                "At least one resource deployment is required.",
                nameof(command));
        }

        var deploymentIds = command.Deployments
            .Select(item => item.ResourceDeploymentId)
            .ToArray();
        if (deploymentIds.Distinct(StringComparer.Ordinal).Count() != deploymentIds.Length)
        {
            throw new ArgumentException(
                "Resource deployment IDs must be unique.",
                nameof(command));
        }

        var current = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForResourceDeploymentIds(
                    command.TenantId,
                    command.ResourceType,
                    command.ResourceId,
                    deploymentIds))
            .ToListAsync(cancellationToken);
        if (current.Count != command.Deployments.Count)
        {
            return Error(new ResourceDeploymentMutationNotFound(command.ResourceId));
        }

        var byId = current.ToDictionary(deployment => deployment.Id, StringComparer.Ordinal);
        foreach (var item in command.Deployments)
        {
            var deployment = byId[item.ResourceDeploymentId];
            if (deployment.Revision != item.ExpectedDeploymentRevision)
            {
                return Error(new ResourceDeploymentChanged(
                    command.ResourceId,
                    item.ResourceDeploymentId));
            }
            var computeExpectation = EnsureExpectedCompute(
                command.ResourceId,
                item.ExpectedCompute,
                deployment);
            if (computeExpectation is not null)
                return Error(computeExpectation.Value);
            if (deployment.DesiredOperation == ResourceDeploymentOperation.Delete ||
                deployment.State is DeletedResourceDeploymentState)
            {
                return Error(new ResourceDeploymentPendingDeletion(
                    command.ResourceId,
                    item.ResourceDeploymentId));
            }
            if (item.ReplacementCompute is not null && !string.Equals(
                    item.ReplacementCompute.DeploymentTargetId,
                    deployment.ResolvedPlacement.DeploymentTargetId,
                    StringComparison.Ordinal))
            {
                return Error(new ResourceDeploymentReplacementTargetMismatch(
                    command.ResourceId,
                    item.ResourceDeploymentId));
            }
            if (item.ReplacementIngress is not null && !HasSameIngressIdentity(
                    deployment.ResolvedPlacement.Ingress,
                    item.ReplacementIngress))
            {
                return Error(new ResourceDeploymentIngressConfigurationChanged(
                    command.ResourceId,
                    item.ResourceDeploymentId));
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
            return Error(new ResourceDeploymentPlacementUnavailable(command.ResourceId));
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
                return Error(new ResourceDeploymentPlacementUnavailable(
                    command.ResourceId,
                    deployment.Id));
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
            var computeFence = await fenceResolvedComputeProfiles(
                computeFenceRequests,
                session,
                cancellationToken);
            if (computeFence is Err<ComputeProfileResolutionFenceRejected>)
            {
                return Error(new ResourceDeploymentComputeConfigurationChanged(
                    computeFenceRequests
                        .Select(request => request.ComputeProfileId)
                        .ToArray()));
            }
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
            var replacement = await resourceDeployments.ReplaceOneAsync(
                session,
                ResourceDeploymentFilters.AtRevision(original),
                updated,
                cancellationToken: cancellationToken);
            if (replacement.ModifiedCount != 1)
            {
                return Error(new ResourceDeploymentChanged(
                    command.ResourceId,
                    original.Id));
            }
        }
        await storeOutboxWrites(
            session,
            updatedDeployments
                .Where(deployment => deployment.State is PendingResourceDeploymentState)
                .Select(deployment => deployment.ToCurrentRequestWrite())
                .ToArray(),
            cancellationToken);

        return Success<IReadOnlyList<PlacementSummaryDto>>(
            updatedDeployments
                .Select(deployment => deployment.ToPlacementSummaryDto())
                .ToArray());
    }

    public async Task<Result<
        IReadOnlyList<PlacementSummaryDto>,
        ResourceDeploymentMutationError>> RequestSetDeletionAsync(
        ResourceDeploymentSetDeletionRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Deployments);
        if (command.Deployments.Any(static deployment => deployment is null))
        {
            throw new ArgumentException(
                "A deployment set cannot contain null entries.",
                nameof(command));
        }
        if (command.Deployments.Count == 0)
        {
            throw new ArgumentException(
                "At least one resource deployment is required.",
                nameof(command));
        }

        var deploymentIds = command.Deployments
            .Select(item => item.ResourceDeploymentId)
            .ToArray();
        if (deploymentIds.Distinct(StringComparer.Ordinal).Count() != deploymentIds.Length)
        {
            throw new ArgumentException(
                "Resource deployment IDs must be unique.",
                nameof(command));
        }

        var current = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForResourceDeploymentIds(
                    command.TenantId,
                    command.ResourceType,
                    command.ResourceId,
                    deploymentIds))
            .ToListAsync(cancellationToken);
        if (current.Count != command.Deployments.Count)
        {
            return Error(new ResourceDeploymentMutationNotFound(command.ResourceId));
        }

        var byId = current.ToDictionary(deployment => deployment.Id, StringComparer.Ordinal);
        foreach (var item in command.Deployments)
        {
            var deployment = byId[item.ResourceDeploymentId];
            if (deployment.Revision != item.ExpectedDeploymentRevision)
            {
                return Error(new ResourceDeploymentChanged(
                    command.ResourceId,
                    item.ResourceDeploymentId));
            }
            var computeExpectation = EnsureExpectedCompute(
                command.ResourceId,
                item.ExpectedCompute,
                deployment);
            if (computeExpectation is not null)
                return Error(computeExpectation.Value);
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
            var replacement = await resourceDeployments.ReplaceOneAsync(
                session,
                ResourceDeploymentFilters.AtRevision(original),
                updated,
                cancellationToken: cancellationToken);
            if (replacement.ModifiedCount != 1)
            {
                return Error(new ResourceDeploymentChanged(
                    command.ResourceId,
                    original.Id));
            }
        }
        await storeOutboxWrites(
            session,
            changedDeployments
                .Where(deployment => deployment.State is PendingResourceDeploymentState)
                .Select(deployment => deployment.ToCurrentRequestWrite())
                .ToArray(),
            cancellationToken);

        return Success<IReadOnlyList<PlacementSummaryDto>>(
            updatedDeployments
                .Select(deployment => deployment.ToPlacementSummaryDto())
                .ToArray());
    }

    private async Task<Result<ResourceDeployment, ResourceDeploymentMutationError>>
        LoadSingleAsync(
        ResourceDeploymentMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ResourceType.Value))
            throw new ArgumentException("Resource type is required.", nameof(command));

        var deployments = await resourceDeployments
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
            0 => Error(new ResourceDeploymentMutationNotFound(command.ResourceId)),
            1 => Success(deployments[0]),
            _ => Error(new ResourceDeploymentCardinalityConflict(command.ResourceId))
        };
    }

    private async Task<Result<ResourceDeployment, ResourceDeploymentMutationError>>
        LoadExactAsync(
        ResourceDeploymentExactMutationRequest command,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ResourceType.Value))
            throw new ArgumentException("Resource type is required.", nameof(command));

        var deployment = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForExactDeployment(
                    command.TenantId,
                    command.ResourceType,
                    command.ResourceId,
                    command.ResourceDeploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        return deployment is null
            ? Error(new ResourceDeploymentMutationNotFound(
                command.ResourceId,
                command.ResourceDeploymentId))
            : Success(deployment);
    }

    private static ResourceDeploymentMutationError? EnsureExpectedCompute(
        ResourceDeploymentMutationRequest command,
        ResourceDeployment current) =>
        EnsureExpectedCompute(command.ResourceId, command.ExpectedCompute, current);

    private static ResourceDeploymentMutationError? EnsureExpectedCompute(
        string resourceId,
        ComputeProfileReference expectedCompute,
        ResourceDeployment current) =>
        current.ResolvedPlacement.Compute.Matches(
            expectedCompute.ComputeProfileId,
            expectedCompute.ComputeProfileRevision)
            ? (ResourceDeploymentMutationError?)null
            : new ResourceDeploymentMutationError(
                new ResourceDeploymentComputeConfigurationChanged(
                    [expectedCompute.ComputeProfileId]));

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

    private async Task<Result<PlacementSummaryDto, ResourceDeploymentMutationError>>
        ReplaceAndEnqueueAsync(
        ResourceDeploymentMutationRequest command,
        ResourceDeployment current,
        ResourceDeployment updated,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var replacement = await resourceDeployments.ReplaceOneAsync(
            session,
            ResourceDeploymentFilters.AtRevision(current),
            updated,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return Error(new ResourceDeploymentChanged(command.ResourceId, current.Id));
        }

        if (updated.State is PendingResourceDeploymentState)
        {
            await storeOutboxWrites(
                session,
                [updated.ToCurrentRequestWrite()],
                cancellationToken);
        }

        return Success(updated.ToPlacementSummaryDto());
    }

    private async Task<Result<PlacementSummaryDto, ResourceDeploymentMutationError>>
        ReplaceExactAndEnqueueAsync(
        ResourceDeployment current,
        ResourceDeployment updated,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var replacement = await resourceDeployments.ReplaceOneAsync(
            session,
            ResourceDeploymentFilters.AtRevision(current),
            updated,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return Error(new ResourceDeploymentChanged(
                current.ResourceId,
                current.Id));
        }

        if (updated.State is PendingResourceDeploymentState)
        {
            await storeOutboxWrites(
                session,
                [updated.ToCurrentRequestWrite()],
                cancellationToken);
        }

        return Success(updated.ToPlacementSummaryDto());
    }

    private static Err<ResourceDeploymentMutationError> Error(
        ResourceDeploymentMutationError error) => new(error);

    private static Ok<T> Success<T>(T value)
        where T : notnull => new(value);

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
