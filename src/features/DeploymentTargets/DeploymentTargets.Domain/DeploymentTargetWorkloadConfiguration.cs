using Pole.Dns;

namespace Pole.Features.DeploymentTargets.Domain;

public sealed record DeploymentTargetWorkloadConfiguration(
    long Revision,
    DeploymentTargetIngressConfiguration? Ingress,
    DeploymentTargetNetworkSources NetworkSources)
{
    public DeploymentTargetNetworkPolicy NetworkPolicy { get; init; } = DeploymentTargetNetworkPolicy.Unknown;

    public static DeploymentTargetWorkloadConfiguration Create(
        DeploymentTargetIngressConfiguration? ingress,
        DeploymentTargetNetworkSources networkSources) =>
        new(Revision: 1, NormalizeIngress(ingress), networkSources.Validate());

    public DeploymentTargetWorkloadConfiguration Reconfigure(
        DeploymentTargetIngressConfiguration? ingress,
        DeploymentTargetNetworkSources networkSources)
    {
        var normalizedIngress = NormalizeIngress(ingress);
        networkSources.Validate();
        return Equals(Ingress, normalizedIngress) && NetworkSources.EquivalentTo(networkSources)
            ? this
            : new DeploymentTargetWorkloadConfiguration(
                checked(DeploymentTargetValueGuard.Positive(Revision, nameof(Revision)) + 1),
                normalizedIngress,
                networkSources)
            {
                NetworkPolicy = NetworkPolicy
            };
    }

    private static DeploymentTargetIngressConfiguration? NormalizeIngress(
        DeploymentTargetIngressConfiguration? ingress) =>
        ingress is null
            ? null
            : DeploymentTargetIngressConfiguration.Create(
                ingress.IngressClassName,
                ingress.ClusterIssuerName,
                ingress.PublicBaseDomain);
}

public sealed record DeploymentTargetIngressConfiguration(
    string IngressClassName,
    string ClusterIssuerName,
    string? PublicBaseDomain = null)
{
    public const int MaximumNameLength = 253;

    public static DeploymentTargetIngressConfiguration Create(
        string ingressClassName,
        string clusterIssuerName,
        string? publicBaseDomain = null) =>
        new(
            DeploymentTargetValueGuard.KubernetesResourceName(
                ingressClassName,
                nameof(ingressClassName)),
            DeploymentTargetValueGuard.KubernetesResourceName(
                clusterIssuerName,
                nameof(clusterIssuerName)),
            NormalizePublicBaseDomain(publicBaseDomain));

    public static bool IsValidPublicBaseDomain(string? value) =>
        !string.IsNullOrWhiteSpace(value) && DnsName.TryCreate(value, out _);

    private static string? NormalizePublicBaseDomain(string? publicBaseDomain)
    {
        if (publicBaseDomain is null)
            return null;

        try
        {
            return DnsName.Create(publicBaseDomain).Value;
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "Public base domain must be a valid ASCII DNS name.",
                nameof(publicBaseDomain),
                exception);
        }
    }

    public static bool IsValidKubernetesResourceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        if (normalized.Length > MaximumNameLength)
            return false;

        var labels = normalized.Split('.');
        return labels.All(IsValidDnsLabel);
    }

    private static bool IsValidDnsLabel(string label)
    {
        if (label.Length is < 1 or > 63 ||
            !IsLowerAlphaNumeric(label[0]) ||
            !IsLowerAlphaNumeric(label[^1]))
        {
            return false;
        }

        return label.All(character =>
            IsLowerAlphaNumeric(character) || character == '-');
    }

    private static bool IsLowerAlphaNumeric(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
