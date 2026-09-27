using System.Collections.Immutable;
using System.Net;
using MongoDB.Bson.Serialization.Attributes;
using Pole.KubernetesValidation;

namespace Pole.Features.DeploymentTargets.Domain;

/// <summary>Platform-owned ingress identities for one deployment target.</summary>
public sealed record DeploymentTargetNetworkSources(
    DeploymentTargetNetworkSource? PublicIngress,
    DeploymentTargetNetworkSource? FunctionGateway,
    DeploymentTargetNetworkSource? PostgresOperator)
{
    public static DeploymentTargetNetworkSources None { get; } = new(null, null, null);

    public bool EquivalentTo(DeploymentTargetNetworkSources other) =>
        DeploymentTargetNetworkSource.Equivalent(PublicIngress, other.PublicIngress) &&
        DeploymentTargetNetworkSource.Equivalent(FunctionGateway, other.FunctionGateway) &&
        DeploymentTargetNetworkSource.Equivalent(PostgresOperator, other.PostgresOperator);
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(DeploymentTargetPodNetworkSource), typeof(DeploymentTargetCidrNetworkSource))]
public abstract record DeploymentTargetNetworkSource
{
    public static bool Equivalent(
        DeploymentTargetNetworkSource? first,
        DeploymentTargetNetworkSource? second) =>
        (first, second) switch
        {
            (null, null) => true,
            (DeploymentTargetPodNetworkSource left, DeploymentTargetPodNetworkSource right) =>
                left.Namespace == right.Namespace &&
                left.PodLabels.Count == right.PodLabels.Count &&
                left.PodLabels.All(label =>
                    right.PodLabels.TryGetValue(label.Key, out var value) && value == label.Value),
            (DeploymentTargetCidrNetworkSource left, DeploymentTargetCidrNetworkSource right) =>
                left.Cidr == right.Cidr,
            _ => false
        };
}

/// <summary>A single Kubernetes NetworkPolicy peer: namespace AND pod labels.</summary>
[BsonDiscriminator(nameof(DeploymentTargetPodNetworkSource))]
public sealed record DeploymentTargetPodNetworkSource(
    string Namespace,
    ImmutableDictionary<string, string> PodLabels) : DeploymentTargetNetworkSource
{
    public static DeploymentTargetPodNetworkSource Create(
        string namespaceName,
        IReadOnlyDictionary<string, string> podLabels)
    {
        if (!IsValidNamespace(namespaceName))
        {
            throw new ArgumentException("Source namespace must be a Kubernetes DNS label.", nameof(namespaceName));
        }
        if (!IsValid(podLabels))
            throw new ArgumentException("At least one pod label is required.", nameof(podLabels));

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in podLabels)
            builder.Add(key, value);

        return new DeploymentTargetPodNetworkSource(namespaceName.Trim(), builder.ToImmutable());
    }

    public static bool IsValidNamespace(string? namespaceName) =>
        !string.IsNullOrWhiteSpace(namespaceName) &&
        namespaceName.Trim().Length <= 63 &&
        !namespaceName.Contains('.') &&
        DeploymentTargetIngressConfiguration.IsValidKubernetesResourceName(namespaceName);

    public static bool IsValid(IReadOnlyDictionary<string, string>? podLabels)
    {
        if (podLabels is null || podLabels.Count == 0)
            return false;

        foreach (var (key, value) in podLabels)
        {
            if (value is null)
                return false;
            try
            {
                var normalizedKey = KubernetesValueGuard.LabelKey(key, nameof(podLabels));
                var normalizedValue = KubernetesValueGuard.LabelValue(value, nameof(podLabels));
                if (!string.Equals(normalizedKey, key, StringComparison.Ordinal) ||
                    !string.Equals(normalizedValue, value, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Explicit source address range for an external platform runtime.</summary>
[BsonDiscriminator(nameof(DeploymentTargetCidrNetworkSource))]
public sealed record DeploymentTargetCidrNetworkSource(string Cidr) : DeploymentTargetNetworkSource
{
    public static DeploymentTargetCidrNetworkSource Create(string cidr)
    {
        if (!IsValid(cidr))
            throw new ArgumentException("Source CIDR must be a bounded IP network.", nameof(cidr));
        IPNetwork.TryParse(cidr, out var network);
        return new DeploymentTargetCidrNetworkSource(network.ToString());
    }

    public static bool IsValid(string? cidr) =>
        !string.IsNullOrWhiteSpace(cidr) &&
        IPNetwork.TryParse(cidr, out var network) &&
        network.PrefixLength > 0;
}
