using Pole.Features.Core.Kubernetes;
using k8s.Autorest;
using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Kubernetes;

public sealed class WebServiceSecretRolloutObserver(
    LoadWebServiceById loadResource,
    LoadResourceDeploymentSet loadDeployments,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement,
    IKubernetesClientProvider clients) : ISecretConsumerRolloutObserver
{
    public ResourceTypeId ResourceType => WebServiceResourceDeployment.ResourceType;

    public async Task<SecretConsumerRolloutOutcome> ObserveAsync(string tenantId, string consumerId, CancellationToken ct)
    {
        var resource = await loadResource(tenantId, consumerId, ct);
        if (resource is null) return new SecretConsumerRolloutPending();
        var deployments = await loadDeployments(tenantId, ResourceType, consumerId, ct);
        if (deployments.Count == 0) return new SecretConsumerRolloutPending();
        var proofs = new List<SecretConsumerDeploymentProof>();
        foreach (var deployment in deployments)
        {
            var placement = await loadPlacement(tenantId, ResourceType, consumerId, deployment.DeploymentId, ct);
            if (placement is null || placement.Status != ResourceDeploymentStatus.Applied ||
                placement.DesiredOperation != ResourceDeploymentDesiredOperation.Apply)
                return new SecretConsumerRolloutPending();
            var identity = new WorkloadIdentityLabels(tenantId, resource.ResourceGroupId, placement.IsolationScopeId,
                placement.RuntimeCellId, placement.DeploymentTargetId,
                WebServiceResourceDeployment.KubernetesResourceTypeLabel, consumerId, placement.DeploymentId);
            var observation = await ObserveNativeAsync(placement, resource.Name, identity, ct);
            if (!ConfirmsRetirement(observation)) return new SecretConsumerRolloutPending();
            proofs.Add(new(placement.DeploymentId, placement.DeploymentRevision, placement.DesiredGeneration));
        }
        return new SecretConsumerRolloutConfirmed(proofs);
    }

    private async Task<WorkloadObservation> ObserveNativeAsync(ResourceDeploymentRuntimePlacementDto placement,
        string name, WorkloadIdentityLabels identity, CancellationToken ct)
    {
        try
        {
            var client = await clients.GetAsync(placement.DeploymentTargetId, ct);
            var observed = await KubernetesWorkloadObservation.DeploymentAsync(client, name,
                placement.KubernetesNamespace, identity, placement.DesiredGeneration, ct);
            if (!ConfirmsRetirement(observed)) return observed;
            // A recreated Deployment has a new UID. Its ready pod summary excludes ReplicaSets
            // from the old controller, so also check every pod with the placement's identity.
            var pods = await client.CoreV1.ListNamespacedPodAsync(placement.KubernetesNamespace,
                labelSelector: string.Join(",", identity.ToDictionary().OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}")),
                limit: KubernetesWorkloadObservation.MaximumPods, cancellationToken: ct);
            if (!string.IsNullOrEmpty(pods.Metadata.ContinueProperty) || pods.Items.Any(pod =>
                    pod.Metadata.NamespaceProperty == placement.KubernetesNamespace &&
                    KubernetesWorkloadObservation.Owns(pod.Metadata, identity) &&
                    (pod.Metadata.DeletionTimestamp is not null ||
                     KubernetesWorkloadObservation.Generation(pod.Metadata, KubernetesWorkloadObservation.TemplateGenerationAnnotation) != placement.DesiredGeneration)))
                return new UnavailableWorkloadObservation("PreviousPodsNotRetired");
            return observed;
        }
        catch (DeploymentTargetUnavailableException) { return new UnavailableWorkloadObservation("TargetUnavailable"); }
        catch (HttpOperationException) { return new UnavailableWorkloadObservation("NativeUnavailable"); }
        catch (HttpRequestException) { return new UnavailableWorkloadObservation("NativeUnavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new UnavailableWorkloadObservation("NativeTimeout"); }
    }

    private static bool ConfirmsRetirement(WorkloadObservation observation) => observation is ObservedWorkload
    { Ready: true, RolloutCurrent: true, PodsTruncated: false } observed &&
        observed.Pods.All(pod => pod.CurrentGeneration);
}
