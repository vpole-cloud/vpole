using k8s.Models;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Kubernetes;

public static class ResolvedComputeConfigurationKubernetesExtensions
{
    private const string ArchitectureLabel = "kubernetes.io/arch";

    public static V1ResourceRequirements ToKubernetesResources(
        this ResolvedComputeConfiguration compute)
    {
        ArgumentNullException.ThrowIfNull(compute);

        var requests = new Dictionary<string, ResourceQuantity>(StringComparer.Ordinal)
        {
            ["cpu"] = new($"{compute.Requests.CpuMillis}m"),
            ["memory"] = new($"{compute.Requests.MemoryMi}Mi")
        };
        var limits = new Dictionary<string, ResourceQuantity>(StringComparer.Ordinal)
        {
            ["cpu"] = new($"{compute.Limits.CpuMillis}m"),
            ["memory"] = new($"{compute.Limits.MemoryMi}Mi")
        };
        foreach (var resource in compute.ExtendedResources)
        {
            var quantity = new ResourceQuantity(resource.Quantity.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            requests.Add(resource.Name, quantity);
            limits.Add(resource.Name, quantity);
        }

        return new V1ResourceRequirements
        {
            Requests = requests,
            Limits = limits
        };
    }

    public static IDictionary<string, string>? ToKubernetesNodeSelector(
        this ResolvedComputeConfiguration compute)
    {
        ArgumentNullException.ThrowIfNull(compute);

        var selector = compute.NodeSelector.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        var architecture = compute.Architecture switch
        {
            ResolvedComputeArchitecture.Any => null,
            ResolvedComputeArchitecture.Amd64 => "amd64",
            ResolvedComputeArchitecture.Arm64 => "arm64",
            _ => throw new ArgumentOutOfRangeException(
                nameof(compute),
                compute.Architecture,
                "Unsupported compute architecture.")
        };
        if (architecture is not null)
        {
            if (selector.TryGetValue(ArchitectureLabel, out var selectedArchitecture) &&
                !string.Equals(selectedArchitecture, architecture, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The compute architecture conflicts with the resolved node selector.");
            }

            selector[ArchitectureLabel] = architecture;
        }

        return selector.Count == 0 ? null : selector;
    }

    public static V1NodeAffinity? ToKubernetesNodeAffinity(
        this ResolvedComputeConfiguration compute)
    {
        ArgumentNullException.ThrowIfNull(compute);

        if (compute.RequiredNodeAffinity.Count == 0 &&
            compute.PreferredNodeAffinity.Count == 0)
        {
            return null;
        }

        return new V1NodeAffinity
        {
            RequiredDuringSchedulingIgnoredDuringExecution =
                compute.RequiredNodeAffinity.Count == 0
                    ? null
                    : new V1NodeSelector
                    {
                        NodeSelectorTerms = compute.RequiredNodeAffinity
                            .Select(ToKubernetesTerm)
                            .ToList()
                    },
            PreferredDuringSchedulingIgnoredDuringExecution =
                compute.PreferredNodeAffinity.Count == 0
                    ? null
                    : compute.PreferredNodeAffinity
                        .Select(term => new V1PreferredSchedulingTerm
                        {
                            Weight = term.Weight,
                            Preference = ToKubernetesTerm(term.Term)
                        })
                        .ToList()
        };
    }

    public static IList<V1Toleration>? ToKubernetesTolerations(
        this ResolvedComputeConfiguration compute)
    {
        ArgumentNullException.ThrowIfNull(compute);

        return compute.Tolerations.Count == 0
            ? null
            : compute.Tolerations
                .Select(toleration => new V1Toleration
                {
                    Key = toleration.Key,
                    OperatorProperty = toleration.Operator switch
                    {
                        ResolvedComputeTolerationOperator.Equal => "Equal",
                        ResolvedComputeTolerationOperator.Exists => "Exists",
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(compute),
                            toleration.Operator,
                            "Unsupported toleration operator.")
                    },
                    Value = toleration.Value,
                    Effect = toleration.Effect switch
                    {
                        ResolvedComputeTaintEffect.Any => null,
                        ResolvedComputeTaintEffect.NoSchedule => "NoSchedule",
                        ResolvedComputeTaintEffect.PreferNoSchedule =>
                            "PreferNoSchedule",
                        ResolvedComputeTaintEffect.NoExecute => "NoExecute",
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(compute),
                            toleration.Effect,
                            "Unsupported taint effect.")
                    },
                    TolerationSeconds = toleration.TolerationSeconds
                })
                .ToList();
    }

    public static void ApplyToPodSpec(
        this ResolvedComputeConfiguration compute,
        V1PodSpec podSpec)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentNullException.ThrowIfNull(podSpec);

        podSpec.NodeSelector = compute.ToKubernetesNodeSelector();
        var nodeAffinity = compute.ToKubernetesNodeAffinity();
        podSpec.Affinity = nodeAffinity is null
            ? null
            : new V1Affinity { NodeAffinity = nodeAffinity };
        podSpec.Tolerations = compute.ToKubernetesTolerations();
        podSpec.RuntimeClassName = compute.RuntimeClassName;
    }

    private static V1NodeSelectorTerm ToKubernetesTerm(
        ResolvedComputeNodeAffinityTerm term) =>
        new()
        {
            MatchExpressions = term.Expressions
                .Select(expression => new V1NodeSelectorRequirement
                {
                    Key = expression.Key,
                    OperatorProperty = expression.Operator switch
                    {
                        ResolvedComputeNodeAffinityOperator.In => "In",
                        ResolvedComputeNodeAffinityOperator.NotIn => "NotIn",
                        ResolvedComputeNodeAffinityOperator.Exists => "Exists",
                        ResolvedComputeNodeAffinityOperator.DoesNotExist =>
                            "DoesNotExist",
                        ResolvedComputeNodeAffinityOperator.GreaterThan => "Gt",
                        ResolvedComputeNodeAffinityOperator.LessThan => "Lt",
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(term),
                            expression.Operator,
                            "Unsupported node-affinity operator.")
                    },
                    Values = expression.Values.Count == 0
                        ? null
                        : expression.Values
                })
                .ToList()
        };
}
