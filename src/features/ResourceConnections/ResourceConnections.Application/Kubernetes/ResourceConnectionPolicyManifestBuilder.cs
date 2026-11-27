// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using k8s.Models;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Features.ResourceConnections.Application.Kubernetes;

public static class ResourceConnectionPolicyManifestBuilder
{
    public const string TenantLabel = "pole.io/tenant-id";
    public const string RuntimeCellLabel = "pole.io/runtime-cell-id";
    public const string DeploymentLabel = "pole.io/resource-deployment-id";
    public const string ConnectionLabel = "pole.io/resource-connection-id";
    public const string GenerationAnnotation = "pole.io/resource-connection-generation";

    public static string PolicyName(string connectionId) => $"pole-connection-{connectionId}";

    public static V1NetworkPolicy Build(ResourceConnection connection, ConnectionTargetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(snapshot);

        return new V1NetworkPolicy
        {
            ApiVersion = "networking.k8s.io/v1",
            Kind = "NetworkPolicy",
            Metadata = new V1ObjectMeta
            {
                Name = PolicyName(connection.Id),
                NamespaceProperty = snapshot.DestinationNamespace,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [TenantLabel] = connection.TenantId,
                    [ConnectionLabel] = connection.Id,
                    [RuntimeCellLabel] = snapshot.DestinationRuntimeCellId,
                },
                Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GenerationAnnotation] = connection.Generation.ToString(CultureInfo.InvariantCulture),
                },
            },
            Spec = new V1NetworkPolicySpec
            {
                PolicyTypes = ["Ingress"],
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [TenantLabel] = connection.TenantId,
                        [RuntimeCellLabel] = snapshot.DestinationRuntimeCellId,
                        [DeploymentLabel] = snapshot.DestinationResourceDeploymentId,
                    },
                },
                Ingress =
                [
                    new V1NetworkPolicyIngressRule
                    {
                        Ports =
                        [
                            new V1NetworkPolicyPort
                            {
                                Protocol = "TCP",
                                Port = snapshot.DestinationPort,
                            },
                        ],
                        FromProperty = snapshot.Sources
                            .Select(source => new V1NetworkPolicyPeer
                            {
                                NamespaceSelector = new V1LabelSelector
                                {
                                    MatchLabels = new Dictionary<string, string>(StringComparer.Ordinal)
                                    {
                                        [TenantLabel] = connection.TenantId,
                                        [RuntimeCellLabel] = source.RuntimeCellId,
                                    },
                                },
                                PodSelector = new V1LabelSelector
                                {
                                    MatchLabels = new Dictionary<string, string>(StringComparer.Ordinal)
                                    {
                                        [TenantLabel] = connection.TenantId,
                                        [DeploymentLabel] = source.ResourceDeploymentId,
                                    },
                                },
                            })
                            .ToList(),
                    },
                ],
            },
        };
    }

    public static bool IsOwned(
        V1NetworkPolicy policy,
        string tenantId,
        string connectionId,
        ConnectionPolicyLocation location) => policy.Metadata?.Labels is { } labels &&
        labels.TryGetValue(TenantLabel, out var actualTenant) && actualTenant == tenantId &&
        labels.TryGetValue(ConnectionLabel, out var actualConnection) && actualConnection == connectionId &&
        labels.TryGetValue(RuntimeCellLabel, out var actualCell) && actualCell == location.DestinationRuntimeCellId &&
        policy.Metadata.NamespaceProperty == location.DestinationNamespace;

    public static bool MatchesDesired(V1NetworkPolicy current, V1NetworkPolicy desired)
    {
        if (current.Metadata?.Annotations is not { } annotations ||
            !annotations.TryGetValue(GenerationAnnotation, out var actualGeneration) ||
            actualGeneration != desired.Metadata.Annotations[GenerationAnnotation] ||
            current.Spec?.PolicyTypes is not { Count: 1 } policyTypes || policyTypes[0] != "Ingress" ||
            !SameLabels(current.Spec.PodSelector?.MatchLabels, desired.Spec.PodSelector.MatchLabels) ||
            !NoExpressions(current.Spec.PodSelector) || current.Spec.Ingress is not { Count: 1 } ingress ||
            ingress[0].Ports is not { Count: 1 } ports || ports[0].Protocol != "TCP" ||
            ports[0].Port?.Value != desired.Spec.Ingress[0].Ports[0].Port.Value || ports[0].EndPort is not null ||
            ingress[0].FromProperty is not { } peers || peers.Count != desired.Spec.Ingress[0].FromProperty.Count)
            return false;

        return peers.Zip(desired.Spec.Ingress[0].FromProperty)
            .All(pair => pair.First.IpBlock is null && NoExpressions(pair.First.NamespaceSelector) &&
                NoExpressions(pair.First.PodSelector) &&
                SameLabels(pair.First.NamespaceSelector?.MatchLabels, pair.Second.NamespaceSelector.MatchLabels) &&
                SameLabels(pair.First.PodSelector?.MatchLabels, pair.Second.PodSelector.MatchLabels));
    }

    private static bool NoExpressions(V1LabelSelector? selector) => selector?.MatchExpressions is null or { Count: 0 };

    private static bool SameLabels(IDictionary<string, string>? current, IDictionary<string, string>? desired) =>
        current is not null && desired is not null && current.Count == desired.Count && desired.All(pair =>
            current.TryGetValue(pair.Key, out var actual) &&
            string.Equals(actual, pair.Value, StringComparison.Ordinal));
}
