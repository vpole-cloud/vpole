// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Net;
using k8s.Models;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.KubernetesValidation;

namespace Pole.Features.ResourceDeployments.Application.Kubernetes;

internal static class ResourceIngressPolicyManifestBuilder
{
    public const string DeploymentGenerationAnnotation = "pole.io/deployment-generation";
    public const string TargetConfigurationRevisionAnnotation = "pole.io/target-config-revision";
    public const string PolicyPurposeLabel = "pole.io/policy-purpose";

    public static string Name(ResourceIngressPolicyRequest request) =>
        $"pole-{request.Purpose}-{request.ResourceDeploymentId}";

    public static V1NetworkPolicy Build(
        ResourceIngressPolicyRequest request,
        ResourceIngressPolicyNetworkSource? source,
        ResourceIngressPolicyDefinition definition)
    {
        var destinationLabels = definition.DestinationPodLabels(request);
        ValidateLabels(destinationLabels);
        if (source is not null)
            ValidateSource(source);

        var labels = OwnershipLabels(request);
        var ingress = source is null || request.AllowedTcpPorts.Count == 0
            ? new List<V1NetworkPolicyIngressRule>()
            :
            [
                new V1NetworkPolicyIngressRule
                {
                    FromProperty = [ToPeer(source)],
                    Ports = request.AllowedTcpPorts
                        .OrderBy(port => port.Value)
                        .Select(port => new V1NetworkPolicyPort
                        {
                            Protocol = "TCP",
                            Port = port.Value,
                        })
                        .ToList(),
                },
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
                        CultureInfo.InvariantCulture),
                },
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = destinationLabels.ToDictionary(
                        label => label.Key,
                        label => label.Value,
                        StringComparer.Ordinal),
                },
                PolicyTypes = ["Ingress"],
                Ingress = ingress,
            },
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
            [PolicyPurposeLabel] = request.Purpose,
        };

    private static ResourceIngressPolicyNetworkSource ValidateSource(ResourceIngressPolicyNetworkSource source) =>
        source switch
        {
            ResourceIngressPolicyPodSource pod when IsValidPodSource(pod) => pod,
            ResourceIngressPolicyPodSource => throw new ArgumentException(
                "Ingress pod source must have an exact DNS namespace and labels.",
                nameof(source)),
            ResourceIngressPolicyCidrSource cidr when IPNetwork.TryParse(cidr.Cidr, out var network) &&
                network.PrefixLength > 0 => cidr,
            ResourceIngressPolicyCidrSource => throw new ArgumentException(
                "Ingress source CIDR must be bounded.",
                nameof(source)),
            null => throw new ArgumentNullException(nameof(source)),
        };

    private static bool IsValidPodSource(ResourceIngressPolicyPodSource pod)
    {
        try
        {
            if (!string.Equals(
                    KubernetesValueGuard.DnsLabel(pod.Namespace, nameof(pod)),
                    pod.Namespace,
                    StringComparison.Ordinal))
                return false;
            ValidateLabels(pod.PodLabels);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateLabels(IReadOnlyDictionary<string, string> labels)
    {
        if (labels.Count == 0)
            throw new ArgumentException("Ingress selectors must contain at least one label.", nameof(labels));
        foreach (var (key, value) in labels)
            if (!string.Equals(KubernetesValueGuard.LabelKey(key, nameof(labels)), key, StringComparison.Ordinal) ||
                !string.Equals(KubernetesValueGuard.LabelValue(value, nameof(labels)), value, StringComparison.Ordinal))
                throw new ArgumentException("Ingress selector labels must be exact Kubernetes labels.", nameof(labels));
    }

    private static V1NetworkPolicyPeer ToPeer(ResourceIngressPolicyNetworkSource source) => source switch
    {
        ResourceIngressPolicyPodSource pod => new V1NetworkPolicyPeer
        {
            NamespaceSelector = new V1LabelSelector
            {
                MatchLabels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kubernetes.io/metadata.name"] = pod.Namespace,
                },
            },
            PodSelector = new V1LabelSelector
            {
                MatchLabels = pod.PodLabels.ToDictionary(
                    label => label.Key,
                    label => label.Value,
                    StringComparer.Ordinal),
            },
        },
        ResourceIngressPolicyCidrSource cidr => new V1NetworkPolicyPeer
        {
            IpBlock = new V1IPBlock { Cidr = cidr.Cidr },
        },
    };
}
