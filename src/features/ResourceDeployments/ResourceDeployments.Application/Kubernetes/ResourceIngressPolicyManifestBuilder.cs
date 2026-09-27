using System.Globalization;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Kubernetes;

internal static class ResourceIngressPolicyManifestBuilder
{
    public const string DeploymentGenerationAnnotation = "pole.io/deployment-generation";
    public const string TargetConfigurationRevisionAnnotation = "pole.io/target-config-revision";
    public const string PolicyPurposeLabel = "pole.io/policy-purpose";

    public static string Name(ResourceIngressPolicyRequest request) =>
        $"pole-{PurposeName(request.Purpose)}-{request.ResourceDeploymentId}";

    public static V1NetworkPolicy Build(
        ResourceIngressPolicyRequest request,
        DeploymentTargetNetworkSourceDto? source)
    {
        var labels = OwnershipLabels(request);
        var ingress = source is null || request.AllowedTcpPorts.Count == 0
            ? new List<V1NetworkPolicyIngressRule>()
            :
            [
                new V1NetworkPolicyIngressRule
                {
                    FromProperty = [ToPeer(source)],
                    Ports = request.AllowedTcpPorts
                        .Order()
                        .Select(port => new V1NetworkPolicyPort
                        {
                            Protocol = "TCP",
                            Port = port
                        })
                        .ToList()
                }
            ];
        return new V1NetworkPolicy
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = Name(request),
                NamespaceProperty = request.KubernetesNamespace,
                Labels = labels,
                Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DeploymentGenerationAnnotation] = request.DeploymentDesiredGeneration.ToString(
                        CultureInfo.InvariantCulture),
                    [TargetConfigurationRevisionAnnotation] = request.TargetWorkloadConfigurationRevision.ToString(
                        CultureInfo.InvariantCulture)
                }
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = request.Purpose == ResourceIngressPolicyPurpose.WebServiceHttp01
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["acme.cert-manager.io/http01-solver"] = "true"
                        }
                        : new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["pole.io/tenant-id"] = request.TenantId,
                            ["pole.io/runtime-cell-id"] = request.RuntimeCellId,
                            ["pole.io/resource-deployment-id"] = request.ResourceDeploymentId
                        }
                },
                PolicyTypes = ["Ingress"],
                Ingress = ingress
            }
        };
    }

    public static Dictionary<string, string> OwnershipLabels(ResourceIngressPolicyRequest request) =>
        new(StringComparer.Ordinal)
        {
            ["pole.io/tenant-id"] = request.TenantId,
            ["pole.io/runtime-cell-id"] = request.RuntimeCellId,
            ["pole.io/deployment-target-id"] = request.DeploymentTargetId,
            ["pole.io/resource-id"] = request.ResourceId,
            ["pole.io/resource-deployment-id"] = request.ResourceDeploymentId,
            [PolicyPurposeLabel] = PurposeName(request.Purpose)
        };

    public static DeploymentTargetNetworkSourceDto ReplicationSource(
        ResourceIngressPolicyRequest request) =>
        new DeploymentTargetPodNetworkSourceDto(
            request.KubernetesNamespace,
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .Add("pole.io/tenant-id", request.TenantId)
                .Add("pole.io/resource-id", request.ResourceId)
                .Add("pole.io/resource-deployment-id", request.ResourceDeploymentId));

    private static V1NetworkPolicyPeer ToPeer(DeploymentTargetNetworkSourceDto source) =>
        source switch
        {
            DeploymentTargetPodNetworkSourceDto pod => new V1NetworkPolicyPeer
            {
                NamespaceSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["kubernetes.io/metadata.name"] = pod.Namespace
                    }
                },
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = pod.PodLabels.ToDictionary(
                        label => label.Key,
                        label => label.Value,
                        StringComparer.Ordinal)
                }
            },
            DeploymentTargetCidrNetworkSourceDto cidr => new V1NetworkPolicyPeer
            {
                IpBlock = new V1IPBlock { Cidr = cidr.Cidr }
            },
            _ => throw new InvalidOperationException("Unsupported target network source.")
        };

    private static string PurposeName(ResourceIngressPolicyPurpose purpose) => purpose switch
    {
        ResourceIngressPolicyPurpose.WebServicePublic => "web-public",
        ResourceIngressPolicyPurpose.WebServiceHttp01 => "web-http01",
        ResourceIngressPolicyPurpose.FunctionGateway => "function-gateway",
        ResourceIngressPolicyPurpose.PostgresOperator => "postgres-operator",
        ResourceIngressPolicyPurpose.PostgresReplication => "postgres-replication",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };
}
