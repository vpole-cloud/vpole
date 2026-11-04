using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Reads;
using Pole.Features.Functions.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Workloads;

namespace Pole.Features.Functions.Runtime.Kubernetes;

public sealed class FunctionSecretRolloutObserver(
    LoadFunctionAppById loadApp,
    LoadFunctionAppRevisions loadRevisions,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement,
    IKubernetesClientProvider clients) : ISecretConsumerRolloutObserver
{
    public ResourceTypeId ResourceType => FunctionResourceDeployment.ResourceType;

    public async Task<SecretConsumerRolloutOutcome> ObserveAsync(
        string tenantId, string consumerId, CancellationToken ct)
    {
        var app = await loadApp(tenantId, consumerId, ct);
        if (app is null || app.DesiredState != FunctionAppDesiredState.Enabled ||
            app.PendingRevisionId is not null || app.ActiveRevisionId is not { } activeId)
            return new SecretConsumerRolloutPending();

        var revisions = await loadRevisions(app, ct);
        var active = revisions.SingleOrDefault(item => item.Id == activeId);
        if (active?.Status is not ActiveFunctionRevisionStatus ||
            revisions.Any(item => item.Id != activeId &&
                item.Status is not (RetiredFunctionRevisionStatus or FailedFunctionRevisionStatus)))
            return new SecretConsumerRolloutPending();

        var placement = await loadPlacement(new LoadExactResourceDeploymentRuntimePlacementRequest(
            tenantId, ResourceType, consumerId, app.Placement.ResourceDeploymentId), ct);
        if (placement is null || placement.Status != ResourceDeploymentStatus.Applied ||
            placement.DesiredOperation != ResourceDeploymentDesiredOperation.Apply ||
            placement.DeploymentTargetId != app.Placement.DeploymentTargetId ||
            placement.RuntimeCellId != app.Placement.RuntimeCellId)
            return new SecretConsumerRolloutPending();

        var identity = new WorkloadIdentityLabels(tenantId, app.ResourceGroupId, placement.IsolationScopeId,
            placement.RuntimeCellId, placement.DeploymentTargetId, "function-app", app.Id, placement.DeploymentId);
        try
        {
            var client = await clients.GetAsync(placement.DeploymentTargetId, ct);
            var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(
                FunctionRevisionInKubernetes.BuildResourceName(app.Id, active.RevisionNumber),
                placement.KubernetesNamespace, cancellationToken: ct);
            var pods = await client.CoreV1.ListNamespacedPodAsync(placement.KubernetesNamespace,
                labelSelector: string.Join(",", identity.ToDictionary()
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}")),
                limit: KubernetesWorkloadObservation.MaximumPods, cancellationToken: ct);
            if (!ConfirmsRetirement(deployment, pods, activeId, placement.KubernetesNamespace, identity))
                return new SecretConsumerRolloutPending();
        }
        catch (DeploymentTargetUnavailableException)
        {
            return new SecretConsumerRolloutPending();
        }
        catch (HttpOperationException)
        {
            return new SecretConsumerRolloutPending();
        }
        catch (HttpRequestException)
        {
            return new SecretConsumerRolloutPending();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SecretConsumerRolloutPending();
        }

        return new SecretConsumerRolloutConfirmed(
            [new(placement.DeploymentId, placement.DeploymentRevision, placement.DesiredGeneration)]);
    }

    private static bool HasRevision(V1ObjectMeta metadata, string revisionId) =>
        metadata.Labels is not null && metadata.Labels.TryGetValue("pole.io/function-revision-id", out var value) &&
        value == revisionId;

    public static bool ConfirmsRetirement(
        V1Deployment deployment,
        V1PodList pods,
        string revisionId,
        string kubernetesNamespace,
        WorkloadIdentityLabels identity)
    {
        var desired = deployment.Spec?.Replicas ?? 1;
        if (deployment.Metadata.NamespaceProperty != kubernetesNamespace ||
            !KubernetesWorkloadObservation.Owns(deployment.Metadata, identity) ||
            !HasRevision(deployment.Metadata, revisionId) ||
            deployment.Metadata.DeletionTimestamp is not null ||
            deployment.Status?.ObservedGeneration < deployment.Metadata.Generation ||
            deployment.Status?.ObservedGeneration is null || deployment.Metadata.Generation is null ||
            (deployment.Status.UpdatedReplicas ?? 0) != desired ||
            (deployment.Status.ReadyReplicas ?? 0) != desired ||
            (deployment.Status.AvailableReplicas ?? 0) != desired ||
            !string.IsNullOrEmpty(pods.Metadata.ContinueProperty))
            return false;

        return pods.Items.Count >= desired && pods.Items.All(pod =>
            pod.Metadata.NamespaceProperty == kubernetesNamespace &&
            KubernetesWorkloadObservation.Owns(pod.Metadata, identity) &&
            HasRevision(pod.Metadata, revisionId) &&
            pod.Metadata.DeletionTimestamp is null &&
            pod.Status?.Conditions?.Any(condition => condition.Type == "Ready" && condition.Status == "True") == true);
    }
}
