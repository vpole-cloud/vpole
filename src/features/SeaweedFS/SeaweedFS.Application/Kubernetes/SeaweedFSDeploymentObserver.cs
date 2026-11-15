using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application.Kubernetes;

internal sealed class SeaweedFSDeploymentObserver(
    LoadSeaweedFSById load,
    IKubernetesClientProvider clients
) : IResourceDeploymentObserver
{
    public async Task<WorkloadObservation> ObserveAsync(
        ResourceDeploymentReconciliationSnapshot deployment,
        CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var service = await load(deployment.TenantId, deployment.ResourceId, ct);
        if (service?.Lifecycle is not ActiveSeaweedFSLifecycle)
            return new UnavailableWorkloadObservation("ResourceNotFound");
        var placement = deployment.Placement;
        var client = await clients.GetAsync(placement.DeploymentTargetId, ct);
        var objects = new SeaweedFSCustomObjects(
            client,
            placement.KubernetesNamespace,
            SeaweedFSDeploymentAdapter.Options(service, deployment).Identity.ToDictionary(),
            deployment.DesiredGeneration);
        var name = SeaweedFSNames.Instance(service.Id);
        var cluster = await objects.ReadAsync(SeaweedFSObjectKind.Cluster, name, ct);
        if (cluster is null)
            return new UnavailableWorkloadObservation("ResourceNotFound");
        var desired = SeaweedFSTopology.For(service.Spec.Mode);
        var status = cluster["status"];
        if (status is null)
            return new UnavailableWorkloadObservation("WaitingForOperator");
        var replicas = new[] { "master", "volume", "filer", "s3" };
        var desiredCount = desired.Masters + desired.Volumes + desired.Filers + desired.Gateways;
        var observed = replicas.Sum(key => (int?)status[key]?["replicas"] ?? 0);
        var ready = replicas.Sum(key => (int?)status[key]?["readyReplicas"] ?? 0);
        var current = (long?)cluster["metadata"]?["generation"] == (long?)status["observedGeneration"] &&
            (string?)cluster["metadata"]?["annotations"]?["pole.io/desired-generation"] ==
            deployment.DesiredGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (service.Spec.Metadata is DedicatedPostgresSeaweedFSMetadata)
        {
            var pg = await objects.ReadAsync(SeaweedFSObjectKind.Postgres, $"{name}-metadata", ct);
            desiredCount += desired.MetadataInstances;
            observed += (int?)pg?["status"]?["instances"] ?? 0;
            ready += (int?)pg?["status"]?["readyInstances"] ?? 0;
        }

        return new ObservedWorkload(
            current && ready == desiredCount && SeaweedFSCustomObjects.Ready(cluster),
            current,
            desiredCount,
            observed,
            current ? observed : 0,
            ready,
            ready,
            false,
            [],
            [],
            null);
    }
}
