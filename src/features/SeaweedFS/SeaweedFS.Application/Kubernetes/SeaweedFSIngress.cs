using System.Collections.Immutable;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SeaweedFS.Application.Common;

namespace Pole.Features.SeaweedFS.Application.Kubernetes;

internal sealed class SeaweedFSIngress(
    ReconcileResourceIngressPolicy reconcile
)
{
    private static ImmutableDictionary<string, string> Labels(ResourceIngressPolicyRequest request) =>
        ImmutableDictionary<string, string>.Empty
            .Add("pole.io/tenant-id", request.TenantId)
            .Add("pole.io/runtime-cell-id", request.RuntimeCellId)
            .Add("pole.io/resource-deployment-id", request.ResourceDeploymentId);

    private static readonly int[] InternalPorts = [5432, 8333, 8444, 8888, 9333, 18444, 18888, 19333];
    private static readonly int[] OperatorPorts = [9333, 8888, 19333, 18888, 18444];
    private static readonly int[] MetadataPorts = [5432, 8000];

    private static ResourceIngressPolicyDefinition Definition(
        string purpose,
        int[] allowed,
        string? source) => new(
        SeaweedFSResourceDeployment.ResourceType,
        purpose,
        false,
        ports => ports.Select(port => port.Value).Order().SequenceEqual(allowed.Order()),
        Labels,
        (request, sources) => source is null
            ? new ResourceIngressPolicyPodSource(request.KubernetesNamespace, Labels(request))
            : sources.Get(source));

    public static IEnumerable<ResourceIngressPolicyDefinition> Definitions =>
    [
        Definition("seaweedfs-internal", InternalPorts, null),
        Definition("seaweedfs-operator", OperatorPorts, "seaweedfs/operator"),
        Definition("seaweedfs-metadata", MetadataPorts, "postgres/operator")
    ];

    public async Task<bool> ApplyAsync(
        ResourceDeploymentReconciliationSnapshot deployment,
        bool replicated,
        bool cleanup,
        CancellationToken ct)
    {
        foreach (var (purpose, ports) in new[]
                 {
                     ("seaweedfs-internal", InternalPorts),
                     ("seaweedfs-operator", OperatorPorts), ("seaweedfs-metadata", MetadataPorts)
                 })
        {
            if (!replicated && purpose == "seaweedfs-metadata")
                continue;
            var request = new ResourceIngressPolicyRequest(
                deployment.TenantId,
                deployment.ResourceType,
                deployment.ResourceId,
                deployment.Id,
                deployment.RuntimeCellId,
                deployment.Placement.DeploymentTargetId,
                deployment.Placement.KubernetesNamespace,
                purpose,
                deployment.DesiredGeneration,
                deployment.Placement.TargetWorkloadConfigurationRevision,
                cleanup ? [] : ports.Select(NetworkPort.Create).ToImmutableList());
            var result = await reconcile(request, ct);
            if (result.Value is not ResourceIngressPolicyApplied)
                return false;
        }

        return true;
    }
}
