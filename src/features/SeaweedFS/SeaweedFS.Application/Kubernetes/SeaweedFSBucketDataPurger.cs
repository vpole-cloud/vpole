using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Lifecycle;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application.Kubernetes;

internal sealed class SeaweedFSBucketDataPurger(
    LoadSeaweedFSById loadInstance,
    LoadSeaweedFSBucket loadBucket,
    LoadResourceDeploymentRuntimePlacements loadPlacements,
    IKubernetesClientProvider clients,
    SeaweedFSBucketReconciler bucketReconciler
)
{
    public async Task<PurgeSeaweedFSBucketDataOutcome> InvokeAsync(
        PurgeSeaweedFSBucketDataRequest request,
        CancellationToken ct)
    {
        var instance = await loadInstance(request.TenantId, request.SeaweedFSId, ct);
        var bucket = await loadBucket(request.TenantId, request.SeaweedFSId, request.BucketId, ct);
        if (instance is null || bucket is null || bucket.Name != request.BucketName ||
            bucket.Lifecycle is not PurgingSeaweedFSLifecycle)
            return new SeaweedFSBucketPurgeRetryableFailure("PurgeIntentUnavailable");
        var placements = await loadPlacements(
            new(request.TenantId, SeaweedFSResourceDeployment.ResourceType, [instance.Id]),
            ct);
        if (placements.Count != 1)
            return new SeaweedFSBucketPurgeRetryableFailure("PlacementUnavailable");
        var placement = placements[0];
        var client = await clients.GetAsync(placement.DeploymentTargetId, ct);
        var identity = new WorkloadIdentityLabels(
            request.TenantId,
            instance.ResourceGroupId,
            placement.IsolationScopeId,
            placement.RuntimeCellId,
            placement.DeploymentTargetId,
            SeaweedFSResourceDeployment.KubernetesResourceTypeLabel,
            instance.Id,
            placement.DeploymentId);
        var objects = new SeaweedFSCustomObjects(
            client,
            placement.KubernetesNamespace,
            identity.ToDictionary(),
            placement.DesiredGeneration);
        return await bucketReconciler.PurgeAsync(objects, bucket, ct)
            ? new SeaweedFSBucketDataPurged()
            : new SeaweedFSBucketPurgeRetryableFailure("WaitingForBucketFinalizers");
    }
}
