using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Placement;

public sealed record CreateResourceDeploymentCommand(
    string DeploymentId,
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceGroupId,
    string? IsolationScopeId,
    ResolvedDeploymentTarget Target,
    ResolvedComputeProfile Compute,
    ResolvedStorageConfiguration? Storage,
    ResourcePlacementCapability PlacementCapability);

public delegate Task<Result<ResourceDeployment>> CreateResourceDeployment(
    CreateResourceDeploymentCommand command,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public interface IResourceDeploymentStorageConfigurationFence
{
    Task<Result> FenceAsync(
        string deploymentTargetId,
        ResolvedStorageConfiguration storage,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

internal sealed class ResourceDeploymentWriter(
    IResourceDeploymentDbContext dbContext,
    IResourcePlacementService placementService,
    IDeploymentTargetWorkloadConfigurationFence targetConfigurationFence,
    IComputeProfileResolutionFence computeProfileFence,
    IResourceDeploymentStorageConfigurationFence storageConfigurationFence,
    TimeProvider timeProvider)
{
    public async Task<Result<ResourceDeployment>> InvokeAsync(
        CreateResourceDeploymentCommand command,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        if (!string.Equals(
                command.Compute.DeploymentTargetId,
                command.Target.DeploymentTargetId,
                StringComparison.Ordinal))
        {
            return Failures.Conflict(
                "The resolved compute profile belongs to a different deployment target.");
        }

        var targetFence = await targetConfigurationFence
            .FenceAsync(
                command.Target.DeploymentTargetId,
                command.Target.WorkloadConfigurationRevision,
                session,
                cancellationToken);
        if (targetFence is not ISuccess)
            return (Failure)targetFence;
        var computeFence = await computeProfileFence.FenceAsync(
            command.Compute,
            command.TenantId,
            command.ResourceType,
            session,
            cancellationToken);
        if (computeFence is not ISuccess)
            return (Failure)computeFence;
        if (command.Storage is not null)
        {
            var storageFence = await storageConfigurationFence.FenceAsync(
                command.Target.DeploymentTargetId,
                command.Storage,
                session,
                cancellationToken);
            if (storageFence is not ISuccess)
                return (Failure)storageFence;
        }

        var placementResult = await placementService.ResolveOrCreateAsync(
            command.TenantId,
            command.ResourceGroupId,
            command.IsolationScopeId,
            command.Target.DeploymentTargetId,
            session,
            cancellationToken);
        if (!placementResult.TryGetValue(out var placement, out var failure))
            return failure;
        if (!string.Equals(
                placement.DeploymentTargetId,
                command.Target.DeploymentTargetId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Resource placement resolved a different deployment target than the fenced target.");
        }
        if (placement.State is ResourcePlacementState.Deleting or
            ResourcePlacementState.Deleted)
        {
            return Failures.Conflict(
                "The selected runtime cell is being deleted.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var resolvedPlacement = ResolvedResourcePlacement.Create(
            placement.RuntimeCellId,
            placement.DeploymentTargetId,
            placement.KubernetesNamespace,
            placement.RuntimeCellGeneration,
            command.Target.WorkloadConfigurationRevision,
            command.Target.Ingress,
            command.Storage,
            command.Compute.ToConfiguration(),
            now);
        var deployment = ResourceDeploymentFactory.Create(
            command.TenantId,
            command.ResourceType,
            command.ResourceId,
            command.ResourceGroupId,
            placement.IsolationScopeId,
            resolvedPlacement,
            command.PlacementCapability,
            runtimeCellReady: placement.State == ResourcePlacementState.Ready,
            new ResourceDeploymentFactory.CreateOptions(
                command.DeploymentId,
                now));
        await dbContext.ResourceDeployments.InsertOneAsync(
            session,
            deployment,
            cancellationToken: cancellationToken);
        if (deployment.State is PendingResourceDeploymentState)
        {
            await dbContext.StoreEventAsync(
                deployment.ToCurrentRequestWrite(),
                session,
                cancellationToken);
        }

        return deployment;
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource deployment creation must run inside the logical resource transaction.");
        }
    }
}
