using MongoDB.Driver;
using Pole.Database.Observation;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain.Observation;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Mongo;

internal static class ObservationStoreFilters
{
    internal static FilterDefinition<ObservationControlDocument> ForControlScope(MongoObservationScope scope) =>
        Builders<ObservationControlDocument>.Filter.Eq(item => item.Id, scope.Id) &
        Builders<ObservationControlDocument>.Filter.Eq(item => item.TenantId, scope.TenantId!);

    internal static FilterDefinition<ObservationDemandDocument> ForDemandScope(MongoObservationScope scope) =>
        Builders<ObservationDemandDocument>.Filter.Eq(item => item.DeploymentId, scope.Id) &
        Builders<ObservationDemandDocument>.Filter.Eq(item => item.TenantId, scope.TenantId!);

    internal static FilterDefinition<ObservationSnapshotDocument> ForSnapshotScope(MongoObservationScope scope) =>
        Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.Id, scope.Id) &
        Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.TenantId, scope.TenantId!);

    internal static FilterDefinition<ObservationControlDocument> ForControlContext(
        ResourceDeploymentObservationContext context) =>
        Builders<ObservationControlDocument>.Filter.Eq(item => item.Pin, ObservationValueMappings.Pin(context));

    internal static FilterDefinition<ObservationSnapshotDocument> ForSnapshotContext(
        ResourceDeploymentObservationContext context) =>
        Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.Pin, ObservationValueMappings.Pin(context));
}

internal static class ResourceDeploymentFilters
{
    public static FilterDefinition<ResourceDeployment> ForOperationState<TState>(
        FilterDefinition<ResourceDeployment> scope) where TState : ResourceDeploymentState =>
        scope & Builders<ResourceDeployment>.Filter.OfType<ResourceDeploymentState, TState>(deployment =>
            deployment.State);

    public static FilterDefinition<ResourceDeployment> ForOperations(
        string tenantId,
        string? groupId,
        string? resourceType,
        string? resourceId)
    {
        var filter = VisibleToTenant(tenantId) & Builders<ResourceDeployment>.Filter.Not(IsDeleted());
        if (groupId is not null)
            filter &= Builders<ResourceDeployment>.Filter.Eq(deployment => deployment.ResourceGroupId, groupId);
        if (resourceType is not null)
            filter &= Builders<ResourceDeployment>.Filter.Eq(deployment => deployment.ResourceType, resourceType);
        if (resourceId is not null)
            filter &= Builders<ResourceDeployment>.Filter.Eq(deployment => deployment.ResourceId, resourceId);
        return filter;
    }

    public static FilterDefinition<ResourceDeployment> ForResourceType(
        ResourceTypeId resourceType)
    {
        EnsureResourceType(resourceType);
        return Builders<ResourceDeployment>.Filter.Eq(
            deployment => deployment.ResourceType,
            resourceType.Value);
    }

    public static FilterDefinition<ResourceDeployment> VisibleToTenant(
        string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return Builders<ResourceDeployment>.Filter.ForTenant(tenantId);
    }

    public static FilterDefinition<ResourceDeployment> ForResource(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId)
    {
        EnsureResourceType(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        return Builders<ResourceDeployment>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceType,
                resourceType.Value),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceId,
                resourceId));
    }

    public static FilterDefinition<ResourceDeployment> ForResources(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        EnsureResourceType(resourceType);
        if (resourceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Resource IDs cannot contain null or whitespace values.",
                nameof(resourceIds));
        }

        return Builders<ResourceDeployment>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceType,
                resourceType.Value),
            Builders<ResourceDeployment>.Filter.In(
                deployment => deployment.ResourceId,
                resourceIds));
    }

    public static FilterDefinition<ResourceDeployment> ForExactDeployment(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string deploymentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        return Builders<ResourceDeployment>.Filter.And(
            ForResource(tenantId, resourceType, resourceId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.Id,
                deploymentId));
    }

    public static FilterDefinition<ResourceDeployment> ForTenantDeployment(
        string tenantId,
        string deploymentId) =>
        Builders<ResourceDeployment>.Filter.And(
            VisibleToTenant(tenantId),
            ByDeploymentId(deploymentId));

    public static FilterDefinition<ResourceDeployment> ForExactDeploymentAtRevision(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string deploymentId,
        long revision) =>
        Builders<ResourceDeployment>.Filter.And(
            ForExactDeployment(tenantId, resourceType, resourceId, deploymentId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.Revision,
                revision));

    public static FilterDefinition<ResourceDeployment> ForResourceDeploymentIds(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IReadOnlyCollection<string> deploymentIds)
    {
        ArgumentNullException.ThrowIfNull(deploymentIds);
        if (deploymentIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Resource deployment IDs cannot contain null or whitespace values.",
                nameof(deploymentIds));
        }

        return Builders<ResourceDeployment>.Filter.And(
            ForResource(tenantId, resourceType, resourceId),
            Builders<ResourceDeployment>.Filter.In(
                deployment => deployment.Id,
                deploymentIds));
    }

    public static FilterDefinition<ResourceDeployment> AtRevision(
        ResourceDeployment deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return Builders<ResourceDeployment>.Filter.And(
            ForResource(
                deployment.TenantId,
                ResourceTypeId.Create(deployment.ResourceType),
                deployment.ResourceId),
            Builders<ResourceDeployment>.Filter.Eq(
                item => item.Id,
                deployment.Id),
            Builders<ResourceDeployment>.Filter.Eq(
                item => item.Revision,
                deployment.Revision));
    }

    public static FilterDefinition<ResourceDeployment> ReferencesComputeProfile(
        string computeProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computeProfileId);
        return Builders<ResourceDeployment>.Filter.Eq(
            deployment => deployment.ResolvedPlacement.Compute.ComputeProfileId,
            computeProfileId);
    }

    public static FilterDefinition<ResourceDeployment> UnfinishedForResourceGroup(
        string tenantId,
        string resourceGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroupId);
        return Builders<ResourceDeployment>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceGroupId,
                resourceGroupId),
            IsUnfinished());
    }

    public static FilterDefinition<ResourceDeployment> UnfinishedForIsolationScope(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(isolationScopeId);
        return Builders<ResourceDeployment>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceGroupId,
                resourceGroupId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.IsolationScopeId,
                isolationScopeId),
            IsUnfinished());
    }

    public static FilterDefinition<ResourceDeployment> ForPendingReconciliation(
        string tenantId,
        string deploymentId,
        string runtimeCellId,
        long generation,
        ResourceDeploymentOperation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeCellId);
        return Builders<ResourceDeployment>.Filter.And(
            ForTenantDeployment(tenantId, deploymentId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.RuntimeCellId,
                runtimeCellId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.DesiredGeneration,
                generation),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.DesiredOperation,
                operation),
            IsPending());
    }

    public static FilterDefinition<ResourceDeployment> ForPendingAtRevision(
        ResourceDeployment deployment) =>
        Builders<ResourceDeployment>.Filter.And(
            AtRevision(deployment),
            IsPending());

    public static FilterDefinition<ResourceDeployment> ForActiveClaimAtRevision(
        ResourceDeployment deployment,
        string claimId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimId);
        return Builders<ResourceDeployment>.Filter.And(
            AtRevision(deployment),
            IsReconciling(),
            Builders<ResourceDeployment>.Filter.Eq(
                item => ((ReconcilingResourceDeploymentState)item.State).ClaimId,
                claimId));
    }

    public static FilterDefinition<ResourceDeployment> ForRecoveryCandidates(
        DateTime now,
        DateTime stalePendingBefore) =>
        Builders<ResourceDeployment>.Filter.Or(
            Builders<ResourceDeployment>.Filter.And(
                Builders<ResourceDeployment>.Filter
                    .OfType<ResourceDeploymentState, AppliedResourceDeploymentState>(deployment => deployment.State),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.DesiredOperation,
                    ResourceDeploymentOperation.Apply),
                Builders<ResourceDeployment>.Filter.Lte(
                    deployment => deployment.UpdatedAtUtc,
                    ResourceDeploymentVerificationSchedule.DueBefore(now))),
            Builders<ResourceDeployment>.Filter.And(
                IsPending(),
                Builders<ResourceDeployment>.Filter.Lte(
                    deployment => deployment.UpdatedAtUtc,
                    stalePendingBefore)),
            Builders<ResourceDeployment>.Filter.And(
                IsReconciling(),
                Builders<ResourceDeployment>.Filter.Lte(
                    deployment =>
                        ((ReconcilingResourceDeploymentState)deployment.State).LeaseExpiresAtUtc,
                    now)),
            Builders<ResourceDeployment>.Filter.And(
                IsRetryableFailure(),
                Builders<ResourceDeployment>.Filter.Lte(
                    deployment =>
                        ((FailedResourceDeploymentState)deployment.State).RetryNotBeforeUtc,
                    now)),
            Builders<ResourceDeployment>.Filter.And(
                IsWaitingForRuntimeCell(),
                Builders<ResourceDeployment>.Filter.Lte(
                    deployment => deployment.UpdatedAtUtc,
                    stalePendingBefore)));

    public static FilterDefinition<ResourceDeployment> ForRecoveryAtRevision(
        ResourceDeployment deployment) =>
        Builders<ResourceDeployment>.Filter.And(
            AtRevision(deployment),
            ForExpectedRecoveryState(deployment.State));

    public static FilterDefinition<ResourceDeployment> ForRetryAtRevision(
        ResourceDeployment deployment) =>
        Builders<ResourceDeployment>.Filter.And(
            AtRevision(deployment),
            IsFailed());

    public static FilterDefinition<ResourceDeployment> WaitingForRuntimeCell(
        string tenantId,
        string runtimeCellId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeCellId);
        return Builders<ResourceDeployment>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.RuntimeCellId,
                runtimeCellId),
            IsWaitingForRuntimeCell());
    }

    public static FilterDefinition<ResourceDeployment> WaitingForRuntimeCellAtRevision(
        ResourceDeployment deployment) =>
        Builders<ResourceDeployment>.Filter.And(
            AtRevision(deployment),
            IsWaitingForRuntimeCell());

    public static FilterDefinition<ResourceDeployment> ByDeploymentId(
        string deploymentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        return Builders<ResourceDeployment>.Filter.Eq(
            deployment => deployment.Id,
            deploymentId);
    }

    private static FilterDefinition<ResourceDeployment> IsUnfinished() =>
        Builders<ResourceDeployment>.Filter.Not(IsDeleted());

    private static FilterDefinition<ResourceDeployment> IsPending() =>
        Builders<ResourceDeployment>.Filter
            .OfType<ResourceDeploymentState, PendingResourceDeploymentState>(deployment => deployment.State);

    private static FilterDefinition<ResourceDeployment> IsReconciling() =>
        Builders<ResourceDeployment>.Filter
            .OfType<ResourceDeploymentState, ReconcilingResourceDeploymentState>(deployment => deployment.State);

    private static FilterDefinition<ResourceDeployment> IsFailed() =>
        Builders<ResourceDeployment>.Filter
            .OfType<ResourceDeploymentState, FailedResourceDeploymentState>(deployment => deployment.State);

    private static FilterDefinition<ResourceDeployment> IsRetryableFailure() =>
        Builders<ResourceDeployment>.Filter.And(
            IsFailed(),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => ((FailedResourceDeploymentState)deployment.State).Retryable,
                true));

    private static FilterDefinition<ResourceDeployment> IsWaitingForRuntimeCell() =>
        Builders<ResourceDeployment>.Filter
            .OfType<ResourceDeploymentState, WaitingForRuntimeCellResourceDeploymentState>(deployment =>
                deployment.State);

    private static FilterDefinition<ResourceDeployment> IsDeleted() =>
        Builders<ResourceDeployment>.Filter
            .OfType<ResourceDeploymentState, DeletedResourceDeploymentState>(deployment => deployment.State);

    private static FilterDefinition<ResourceDeployment> ForExpectedRecoveryState(
        ResourceDeploymentState state) =>
        state switch
        {
            AppliedResourceDeploymentState =>
                Builders<ResourceDeployment>.Filter
                    .OfType<ResourceDeploymentState, AppliedResourceDeploymentState>(deployment => deployment.State),
            WaitingForRuntimeCellResourceDeploymentState => IsWaitingForRuntimeCell(),
            PendingResourceDeploymentState => IsPending(),
            ReconcilingResourceDeploymentState reconciling =>
                Builders<ResourceDeployment>.Filter.And(
                    IsReconciling(),
                    Builders<ResourceDeployment>.Filter.Eq(
                        deployment =>
                            ((ReconcilingResourceDeploymentState)deployment.State).ClaimId,
                        reconciling.ClaimId)),
            FailedResourceDeploymentState => IsFailed(),
            _ => throw new InvalidOperationException(
                $"State '{state.GetType().Name}' cannot be recovered.")
        };

    private static void EnsureResourceType(ResourceTypeId resourceType)
    {
        if (string.IsNullOrWhiteSpace(resourceType.Value))
            throw new ArgumentException("Resource type is required.", nameof(resourceType));
    }

    internal static FilterDefinition<ResourceDeployment> ForCurrentObservation(
        ResourceDeploymentObservationContext context)
    {
        var deployment = context.Deployment;
        return ForExactDeploymentAtRevision(deployment.TenantId,
                   deployment.ResourceType,
                   deployment.ResourceId,
                   deployment.Id,
                   context.DeploymentRevision) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.DesiredGeneration, deployment.DesiredGeneration) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResourceRevision, deployment.ResourceRevision) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.FixedReplicas, deployment.FixedReplicas) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResourceGroupId, deployment.ResourceGroupId) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.IsolationScopeId, deployment.IsolationScopeId) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.RuntimeCellId, deployment.RuntimeCellId) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResolvedPlacement.DeploymentTargetId,
                   deployment.Placement.DeploymentTargetId) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResolvedPlacement.KubernetesNamespace,
                   deployment.Placement.KubernetesNamespace) &
               Builders<ResourceDeployment>.Filter.Eq(item =>
                       item.ResolvedPlacement.TargetWorkloadConfigurationRevision,
                   deployment.Placement.TargetWorkloadConfigurationRevision) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResolvedPlacement.Compute,
                   deployment.Placement.Compute.ToDomain()) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResolvedPlacement.Ingress,
                   deployment.Placement.Ingress.ToDomain()) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResolvedPlacement.Storage,
                   deployment.Placement.Storage.ToDomain()) &
               Builders<ResourceDeployment>.Filter.Not(Builders<ResourceDeployment>.Filter
                   .OfType<ResourceDeploymentState, DeletedResourceDeploymentState>(item => item.State)) &
               Builders<ResourceDeployment>.Filter.Lt(item => item.ReferenceFence, long.MaxValue);
    }
}
