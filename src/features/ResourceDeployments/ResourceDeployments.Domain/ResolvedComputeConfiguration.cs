// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.KubernetesValidation;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Domain;

public sealed record ResolvedComputeResourceValues(
    long CpuMillis,
    long MemoryMi
)
{
    public static ResolvedComputeResourceValues Create(long cpuMillis, long memoryMi) => new(
        ResourceDeploymentValueGuard.Positive(cpuMillis, nameof(cpuMillis)),
        ResourceDeploymentValueGuard.Positive(memoryMi, nameof(memoryMi)));
}

public sealed record ResolvedComputeExtendedResource(
    string Name,
    long Quantity
)
{
    public static ResolvedComputeExtendedResource Create(string name, long quantity) => new(
        KubernetesValueGuard.ExtendedResourceName(name, nameof(name)),
        ResourceDeploymentValueGuard.Positive(quantity, nameof(quantity)));
}

public sealed record ResolvedComputeNodeSelector(
    string Key,
    string Value
)
{
    public static ResolvedComputeNodeSelector Create(string key, string value) => new(
        KubernetesValueGuard.LabelKey(key, nameof(key)),
        KubernetesValueGuard.LabelValue(value, nameof(value)));
}

public sealed record ResolvedComputeNodeAffinityExpression(
    string Key,
    ComputeNodeAffinityOperator Operator,
    ImmutableList<string> Values
)
{
    public static ResolvedComputeNodeAffinityExpression Create(
        string key,
        ComputeNodeAffinityOperator @operator,
        IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Enum.IsDefined(@operator))
            throw new ArgumentOutOfRangeException(nameof(@operator));

        var normalizedValues = values.Select(value => KubernetesValueGuard.LabelValue(value, nameof(values)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableList();
        var expectedValueCount = @operator switch
        {
            ComputeNodeAffinityOperator.In or ComputeNodeAffinityOperator.NotIn => normalizedValues.Count > 0,
            ComputeNodeAffinityOperator.Exists or ComputeNodeAffinityOperator.DoesNotExist => normalizedValues.Count ==
                0,
            ComputeNodeAffinityOperator.GreaterThan or ComputeNodeAffinityOperator.LessThan =>
                normalizedValues.Count == 1 && long.TryParse(normalizedValues[0], out _),
            _ => false,
        };
        if (!expectedValueCount)
            throw new ArgumentException("Node-affinity values do not match the selected operator.", nameof(values));

        return new ResolvedComputeNodeAffinityExpression(
            KubernetesValueGuard.LabelKey(key, nameof(key)),
            @operator,
            normalizedValues);
    }
}

public sealed record ResolvedComputeNodeAffinityTerm(
    ImmutableList<ResolvedComputeNodeAffinityExpression> Expressions
)
{
    public static ResolvedComputeNodeAffinityTerm Create(IEnumerable<ResolvedComputeNodeAffinityExpression> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        var normalized = expressions
            .Select(expression => expression ?? throw new ArgumentException(
                "Node-affinity expressions cannot contain null values.",
                nameof(expressions)))
            .Select(expression => ResolvedComputeNodeAffinityExpression.Create(
                expression.Key,
                expression.Operator,
                expression.Values))
            .OrderBy(expression => expression.Key, StringComparer.Ordinal)
            .ThenBy(expression => expression.Operator)
            .ThenBy(expression => string.Join(',', expression.Values), StringComparer.Ordinal)
            .ToImmutableList();
        if (normalized.Count == 0)
            throw new ArgumentException("A node-affinity term must contain an expression.", nameof(expressions));

        return new ResolvedComputeNodeAffinityTerm(normalized);
    }
}

public sealed record ResolvedComputePreferredNodeAffinityTerm(
    int Weight,
    ResolvedComputeNodeAffinityTerm Term
)
{
    public static ResolvedComputePreferredNodeAffinityTerm Create(int weight, ResolvedComputeNodeAffinityTerm term)
    {
        if (weight is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(weight));
        ArgumentNullException.ThrowIfNull(term);

        return new ResolvedComputePreferredNodeAffinityTerm(
            weight,
            ResolvedComputeNodeAffinityTerm.Create(term.Expressions));
    }
}

public sealed record ResolvedComputeToleration(
    string? Key,
    ComputeTolerationOperator Operator,
    string? Value,
    ComputeTaintEffect Effect,
    long? TolerationSeconds
)
{
    public static ResolvedComputeToleration Create(
        string? key,
        ComputeTolerationOperator @operator,
        string? value,
        ComputeTaintEffect effect,
        long? tolerationSeconds)
    {
        if (!Enum.IsDefined(@operator))
            throw new ArgumentOutOfRangeException(nameof(@operator));
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect));
        if (tolerationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerationSeconds));

        var normalizedKey = string.IsNullOrWhiteSpace(key) ? null : KubernetesValueGuard.LabelKey(key, nameof(key));
        var normalizedValue = value is null ? null : KubernetesValueGuard.LabelValue(value, nameof(value));
        if (@operator == ComputeTolerationOperator.Equal && (normalizedKey is null || normalizedValue is null))
            throw new ArgumentException("Equal tolerations require both key and value.", nameof(value));
        if (@operator == ComputeTolerationOperator.Exists && normalizedValue is not null)
            throw new ArgumentException("Exists tolerations cannot specify a value.", nameof(value));
        if (normalizedKey is null && @operator != ComputeTolerationOperator.Exists)
            throw new ArgumentException("An empty toleration key requires the Exists operator.", nameof(key));
        if (tolerationSeconds is not null && effect != ComputeTaintEffect.NoExecute)
            throw new ArgumentException(
                "Toleration seconds are supported only for NoExecute tolerations.",
                nameof(tolerationSeconds));

        return new ResolvedComputeToleration(normalizedKey, @operator, normalizedValue, effect, tolerationSeconds);
    }
}

public sealed record ResolvedComputeConfiguration(
    [property: BsonRepresentation(BsonType.ObjectId)]
    string ComputeProfileId,
    long ComputeProfileRevision,
    string DisplayName,
    ResolvedComputeResourceValues Requests,
    ResolvedComputeResourceValues Limits,
    ComputeArchitecture Architecture,
    ImmutableList<ResolvedComputeExtendedResource> ExtendedResources,
    ImmutableList<ResolvedComputeNodeSelector> NodeSelector,
    ImmutableList<ResolvedComputeNodeAffinityTerm> RequiredNodeAffinity,
    ImmutableList<ResolvedComputePreferredNodeAffinityTerm> PreferredNodeAffinity,
    ImmutableList<ResolvedComputeToleration> Tolerations,
    string? RuntimeClassName
)
{
    public static ResolvedComputeConfiguration Create(
        string computeProfileId,
        long computeProfileRevision,
        string displayName,
        ResolvedComputeResourceValues requests,
        ResolvedComputeResourceValues limits,
        ComputeArchitecture architecture,
        IEnumerable<ResolvedComputeExtendedResource>? extendedResources = null,
        IEnumerable<ResolvedComputeNodeSelector>? nodeSelector = null,
        IEnumerable<ResolvedComputeNodeAffinityTerm>? requiredNodeAffinity = null,
        IEnumerable<ResolvedComputePreferredNodeAffinityTerm>? preferredNodeAffinity = null,
        IEnumerable<ResolvedComputeToleration>? tolerations = null,
        string? runtimeClassName = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(limits);
        if (!Enum.IsDefined(architecture))
            throw new ArgumentOutOfRangeException(nameof(architecture));
        var normalizedRequests = ResolvedComputeResourceValues.Create(requests.CpuMillis, requests.MemoryMi);
        var normalizedLimits = ResolvedComputeResourceValues.Create(limits.CpuMillis, limits.MemoryMi);
        if (normalizedLimits.CpuMillis < normalizedRequests.CpuMillis ||
            normalizedLimits.MemoryMi < normalizedRequests.MemoryMi)
            throw new ArgumentException("Compute limits cannot be lower than requests.", nameof(limits));
        var normalizedExtendedResources = NormalizeExtendedResources(extendedResources);
        var normalizedNodeSelector = NormalizeNodeSelector(nodeSelector);
        EnsureArchitectureMatchesNodeSelector(architecture, normalizedNodeSelector);

        return new ResolvedComputeConfiguration(
            ResourceDeploymentValueGuard.ObjectId(computeProfileId, nameof(computeProfileId)),
            ResourceDeploymentValueGuard.Positive(computeProfileRevision, nameof(computeProfileRevision)),
            ResourceDeploymentValueGuard.Required(
                displayName,
                nameof(displayName),
                ResourceDeploymentValueGuard.MaximumDisplayNameLength),
            normalizedRequests,
            normalizedLimits,
            architecture,
            normalizedExtendedResources,
            normalizedNodeSelector,
            (requiredNodeAffinity ?? []).Select(term => ResolvedComputeNodeAffinityTerm.Create(term.Expressions))
            .OrderBy(TermSortKey, StringComparer.Ordinal)
            .ToImmutableList(),
            (preferredNodeAffinity ?? [])
            .Select(term => ResolvedComputePreferredNodeAffinityTerm.Create(term.Weight, term.Term))
            .OrderByDescending(term => term.Weight)
            .ThenBy(term => TermSortKey(term.Term), StringComparer.Ordinal)
            .ToImmutableList(),
            (tolerations ?? []).Select(toleration => ResolvedComputeToleration.Create(
                toleration.Key,
                toleration.Operator,
                toleration.Value,
                toleration.Effect,
                toleration.TolerationSeconds))
            .OrderBy(TolerationSortKey, StringComparer.Ordinal)
            .ToImmutableList(),
            KubernetesValueGuard.OptionalDnsSubdomain(runtimeClassName, nameof(runtimeClassName)));
    }

    public bool Matches(string computeProfileId, long computeProfileRevision) =>
        string.Equals(ComputeProfileId, computeProfileId, StringComparison.Ordinal) &&
        ComputeProfileRevision == computeProfileRevision;

    private static ImmutableList<ResolvedComputeExtendedResource> NormalizeExtendedResources(
        IEnumerable<ResolvedComputeExtendedResource>? extendedResources)
    {
        var normalized = (extendedResources ?? [])
            .Select(resource => ResolvedComputeExtendedResource.Create(resource.Name, resource.Quantity))
            .OrderBy(resource => resource.Name, StringComparer.Ordinal)
            .ToImmutableList();
        if (normalized.Select(resource => resource.Name).Distinct(StringComparer.Ordinal).Count() != normalized.Count)
            throw new ArgumentException("Extended resource names must be unique.", nameof(extendedResources));

        return normalized;
    }

    private static ImmutableList<ResolvedComputeNodeSelector> NormalizeNodeSelector(
        IEnumerable<ResolvedComputeNodeSelector>? nodeSelector)
    {
        var normalized = (nodeSelector ?? [])
            .Select(selector => ResolvedComputeNodeSelector.Create(selector.Key, selector.Value))
            .OrderBy(selector => selector.Key, StringComparer.Ordinal)
            .ThenBy(selector => selector.Value, StringComparer.Ordinal)
            .ToImmutableList();
        if (normalized.Select(selector => selector.Key).Distinct(StringComparer.Ordinal).Count() != normalized.Count)
            throw new ArgumentException("Node selector keys must be unique.", nameof(nodeSelector));

        return normalized;
    }

    private static string TermSortKey(ResolvedComputeNodeAffinityTerm term) => string.Join(
        '|',
        term.Expressions.Select(expression =>
            $"{expression.Key}:{expression.Operator}:{string.Join(',', expression.Values)}"));

    private static string TolerationSortKey(ResolvedComputeToleration toleration) =>
        $"{toleration.Key}:{toleration.Operator}:{toleration.Value}:" +
        $"{toleration.Effect}:{toleration.TolerationSeconds}";

    private static void EnsureArchitectureMatchesNodeSelector(
        ComputeArchitecture architecture,
        ImmutableList<ResolvedComputeNodeSelector> nodeSelector)
    {
        var expected = architecture switch
        {
            ComputeArchitecture.Any => null,
            ComputeArchitecture.Amd64 => "amd64",
            ComputeArchitecture.Arm64 => "arm64",
            _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
        };
        var selected = nodeSelector.FirstOrDefault(selector => string.Equals(
            selector.Key,
            "kubernetes.io/arch",
            StringComparison.Ordinal));
        if (expected is not null && selected is not null &&
            !string.Equals(selected.Value, expected, StringComparison.Ordinal))
            throw new ArgumentException(
                "The compute architecture conflicts with the node selector.",
                nameof(nodeSelector));
    }
}
