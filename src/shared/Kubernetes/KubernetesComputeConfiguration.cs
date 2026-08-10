using k8s.Models;
using Pole.Workloads;

namespace Pole.Features.Core.Kubernetes;

public sealed record KubernetesComputeResourceValues(
    long CpuMillis,
    long MemoryMi);

public sealed record KubernetesExtendedResource(
    string Name,
    long Quantity);

public sealed record KubernetesNodeSelector(
    string Key,
    string Value);

public sealed record KubernetesNodeAffinityExpression(
    string Key,
    ComputeNodeAffinityOperator Operator,
    IReadOnlyList<string> Values);

public sealed record KubernetesNodeAffinityTerm(
    IReadOnlyList<KubernetesNodeAffinityExpression> Expressions);

public sealed record KubernetesPreferredNodeAffinityTerm(
    int Weight,
    KubernetesNodeAffinityTerm Term);

public sealed record KubernetesToleration(
    string? Key,
    ComputeTolerationOperator Operator,
    string? Value,
    ComputeTaintEffect Effect,
    long? TolerationSeconds);

/// <summary>
/// Dependency-neutral input for rendering workload compute configuration to Kubernetes objects.
/// Feature contracts must be mapped into this technical representation at the adopter boundary.
/// </summary>
public sealed record KubernetesComputeConfiguration(
    KubernetesComputeResourceValues Requests,
    KubernetesComputeResourceValues Limits,
    ComputeArchitecture Architecture,
    IReadOnlyList<KubernetesExtendedResource> ExtendedResources,
    IReadOnlyList<KubernetesNodeSelector> NodeSelector,
    IReadOnlyList<KubernetesNodeAffinityTerm> RequiredNodeAffinity,
    IReadOnlyList<KubernetesPreferredNodeAffinityTerm> PreferredNodeAffinity,
    IReadOnlyList<KubernetesToleration> Tolerations,
    string? RuntimeClassName)
{
    private const string ArchitectureLabel = "kubernetes.io/arch";

    public V1ResourceRequirements ToResourceRequirements()
    {
        var requests = new Dictionary<string, ResourceQuantity>(StringComparer.Ordinal)
        {
            ["cpu"] = new($"{Requests.CpuMillis}m"),
            ["memory"] = new($"{Requests.MemoryMi}Mi")
        };
        var limits = new Dictionary<string, ResourceQuantity>(StringComparer.Ordinal)
        {
            ["cpu"] = new($"{Limits.CpuMillis}m"),
            ["memory"] = new($"{Limits.MemoryMi}Mi")
        };
        foreach (var resource in ExtendedResources)
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

    public IDictionary<string, string>? ToNodeSelector()
    {
        var selector = NodeSelector.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        var architecture = Architecture switch
        {
            ComputeArchitecture.Any => null,
            ComputeArchitecture.Amd64 => "amd64",
            ComputeArchitecture.Arm64 => "arm64",
            _ => throw new ArgumentOutOfRangeException(
                nameof(Architecture),
                Architecture,
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

    public V1NodeAffinity? ToNodeAffinity()
    {
        if (RequiredNodeAffinity.Count == 0 && PreferredNodeAffinity.Count == 0)
            return null;

        return new V1NodeAffinity
        {
            RequiredDuringSchedulingIgnoredDuringExecution =
                RequiredNodeAffinity.Count == 0
                    ? null
                    : new V1NodeSelector
                    {
                        NodeSelectorTerms = RequiredNodeAffinity
                            .Select(ToKubernetesTerm)
                            .ToList()
                    },
            PreferredDuringSchedulingIgnoredDuringExecution =
                PreferredNodeAffinity.Count == 0
                    ? null
                    : PreferredNodeAffinity
                        .Select(term => new V1PreferredSchedulingTerm
                        {
                            Weight = term.Weight,
                            Preference = ToKubernetesTerm(term.Term)
                        })
                        .ToList()
        };
    }

    public IList<V1Toleration>? ToTolerations() =>
        Tolerations.Count == 0
            ? null
            : Tolerations.Select(toleration => new V1Toleration
            {
                Key = toleration.Key,
                OperatorProperty = toleration.Operator switch
                {
                    ComputeTolerationOperator.Equal => "Equal",
                    ComputeTolerationOperator.Exists => "Exists",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(Tolerations),
                        toleration.Operator,
                        "Unsupported toleration operator.")
                },
                Value = toleration.Value,
                Effect = toleration.Effect switch
                {
                    ComputeTaintEffect.Any => null,
                    ComputeTaintEffect.NoSchedule => "NoSchedule",
                    ComputeTaintEffect.PreferNoSchedule => "PreferNoSchedule",
                    ComputeTaintEffect.NoExecute => "NoExecute",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(Tolerations),
                        toleration.Effect,
                        "Unsupported taint effect.")
                },
                TolerationSeconds = toleration.TolerationSeconds
            }).ToList();

    public void ApplyTo(V1PodSpec podSpec)
    {
        ArgumentNullException.ThrowIfNull(podSpec);

        podSpec.NodeSelector = ToNodeSelector();
        var nodeAffinity = ToNodeAffinity();
        podSpec.Affinity = nodeAffinity is null
            ? null
            : new V1Affinity { NodeAffinity = nodeAffinity };
        podSpec.Tolerations = ToTolerations();
        podSpec.RuntimeClassName = RuntimeClassName;
    }

    private static V1NodeSelectorTerm ToKubernetesTerm(
        KubernetesNodeAffinityTerm term) =>
        new()
        {
            MatchExpressions = term.Expressions.Select(expression =>
                new V1NodeSelectorRequirement
                {
                    Key = expression.Key,
                    OperatorProperty = expression.Operator switch
                    {
                        ComputeNodeAffinityOperator.In => "In",
                        ComputeNodeAffinityOperator.NotIn => "NotIn",
                        ComputeNodeAffinityOperator.Exists => "Exists",
                        ComputeNodeAffinityOperator.DoesNotExist => "DoesNotExist",
                        ComputeNodeAffinityOperator.GreaterThan => "Gt",
                        ComputeNodeAffinityOperator.LessThan => "Lt",
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(term),
                            expression.Operator,
                            "Unsupported node-affinity operator.")
                    },
                    Values = expression.Values.Count == 0
                        ? null
                        : expression.Values.ToList()
                }).ToList()
        };
}
