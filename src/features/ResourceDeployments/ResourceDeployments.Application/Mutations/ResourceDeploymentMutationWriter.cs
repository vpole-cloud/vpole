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
    : IResourceDeploymentMutationWriter
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

    private static ConflictFailure? EnsureExpectedCompute(
        ResourceDeploymentMutationRequest command,
        ResourceDeployment current) =>
        current.ResolvedPlacement.Compute.Matches(
            command.ExpectedCompute.ComputeProfileId,
            command.ExpectedCompute.ComputeProfileRevision)
            ? null
            : Failures.Conflict(
                "The resource deployment compute configuration changed while the logical resource mutation was being prepared.");

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
