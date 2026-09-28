using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Common;

public enum DeploymentTargetBootstrapMode
{
    Auto,
    InCluster,
    Kubeconfig
}

public sealed class DeploymentTargetOptions
{
    public const string SectionName = "DeploymentTargets";

    public int ValidationTimeoutSeconds { get; init; } = 15;
    public DeploymentTargetBootstrapOptions Bootstrap { get; init; } = new();
}

public sealed class DeploymentTargetBootstrapOptions
{
    public bool Enabled { get; init; } = true;
    public string DisplayName { get; init; } = "Local cluster";
    public DeploymentTargetBootstrapMode Mode { get; init; } = DeploymentTargetBootstrapMode.Auto;
    public string? KubeconfigPath { get; init; }
    public string? KubeconfigContext { get; init; }
    public DeploymentTargetBootstrapWorkloadConfigurationOptions WorkloadConfiguration { get; init; } =
        new();
}

public sealed class DeploymentTargetBootstrapWorkloadConfigurationOptions
{
    public DeploymentTargetBootstrapIngressOptions? Ingress { get; init; } = new();
    public DeploymentTargetBootstrapNetworkSourcesOptions NetworkSources { get; init; } = new();
}

// Separate typed maps let IConfiguration bind each variant without a discriminator and
// nullable variant fields. A name may occur in only one map, enforced at startup.
public sealed class DeploymentTargetBootstrapNetworkSourcesOptions
{
    public Dictionary<string, DeploymentTargetBootstrapPodSourceOptions> Pods { get; init; } = [];
    public Dictionary<string, string> Cidrs { get; init; } = [];

    internal DeploymentTargetNetworkSources ToDomain() => new(
        Pods.Select(entry => new KeyValuePair<string, DeploymentTargetNetworkSource>(
                entry.Key,
                DeploymentTargetPodNetworkSource.Create(entry.Value.Namespace, entry.Value.PodLabels)))
            .Concat(Cidrs.Select(entry => new KeyValuePair<string, DeploymentTargetNetworkSource>(
                entry.Key,
                DeploymentTargetCidrNetworkSource.Create(entry.Value))))
            .ToImmutableDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));

    internal IEnumerable<string> ValidationFailures()
    {
        const string prefix = "DeploymentTargets:Bootstrap:WorkloadConfiguration:NetworkSources";
        foreach (var sourceName in Pods.Keys.Concat(Cidrs.Keys).Distinct())
        {
            if (!DeploymentTargetNetworkSources.IsValidName(sourceName))
                yield return $"{prefix}: source name '{sourceName}' is invalid.";
            if (Pods.ContainsKey(sourceName) && Cidrs.ContainsKey(sourceName))
                yield return $"{prefix}: '{sourceName}' must have exactly one source variant.";
        }

        foreach (var (purpose, pod) in Pods)
        {
            if (pod is null || !DeploymentTargetPodNetworkSource.IsValidNamespace(pod.Namespace) ||
                !DeploymentTargetPodNetworkSource.IsValid(pod.PodLabels))
            {
                yield return $"{prefix}:Pods:{purpose} requires an exact namespace and nonempty valid pod labels.";
            }
        }

        foreach (var (purpose, cidr) in Cidrs)
        {
            if (!DeploymentTargetCidrNetworkSource.IsValid(cidr))
                yield return $"{prefix}:Cidrs:{purpose} must be a bounded IP network.";
        }
    }
}

public sealed class DeploymentTargetBootstrapPodSourceOptions
{
    public string Namespace { get; init; } = string.Empty;
    public Dictionary<string, string> PodLabels { get; init; } = [];
}

public sealed class DeploymentTargetBootstrapIngressOptions
{
    public string IngressClassName { get; init; } = "traefik";
    public string ClusterIssuerName { get; init; } = "letsencrypt-prod";
    public string? PublicBaseDomain { get; init; }
}

public sealed class DeploymentTargetOptionsValidator : IValidateOptions<DeploymentTargetOptions>
{
    public ValidateOptionsResult Validate(string? name, DeploymentTargetOptions options)
    {
        var failures = new List<string>();
        if (options.ValidationTimeoutSeconds is < 1 or > 120)
            failures.Add("DeploymentTargets:ValidationTimeoutSeconds must be between 1 and 120.");

        if (!Enum.IsDefined(options.Bootstrap.Mode))
            failures.Add("DeploymentTargets:Bootstrap:Mode is not supported.");

        if (string.IsNullOrWhiteSpace(options.Bootstrap.DisplayName) ||
            options.Bootstrap.DisplayName.Trim().Length > 80)
        {
            failures.Add("DeploymentTargets:Bootstrap:DisplayName is required and cannot exceed 80 characters.");
        }

        if (!string.IsNullOrWhiteSpace(options.Bootstrap.KubeconfigPath) &&
            !Path.IsPathFullyQualified(options.Bootstrap.KubeconfigPath))
        {
            failures.Add("DeploymentTargets:Bootstrap:KubeconfigPath must be an absolute path.");
        }

        if (options.Bootstrap.Mode == DeploymentTargetBootstrapMode.InCluster &&
            (!string.IsNullOrWhiteSpace(options.Bootstrap.KubeconfigPath) ||
             !string.IsNullOrWhiteSpace(options.Bootstrap.KubeconfigContext)))
        {
            failures.Add("InCluster bootstrap cannot specify kubeconfig path or context.");
        }

        var workloadConfiguration = options.Bootstrap.WorkloadConfiguration;
        if (workloadConfiguration is null)
        {
            failures.Add(
                "DeploymentTargets:Bootstrap:WorkloadConfiguration is required.");
        }
        else
        {
            failures.AddRange(workloadConfiguration.NetworkSources.ValidationFailures());
            var ingress = workloadConfiguration.Ingress;
            if (ingress is not null &&
                !DeploymentTargetIngressConfiguration.IsValidKubernetesResourceName(
                    ingress.IngressClassName))
            {
                failures.Add(
                    "DeploymentTargets:Bootstrap:WorkloadConfiguration:Ingress:IngressClassName " +
                    "must be a valid Kubernetes resource name.");
            }
            if (ingress is not null &&
                !DeploymentTargetIngressConfiguration.IsValidKubernetesResourceName(
                    ingress.ClusterIssuerName))
            {
                failures.Add(
                    "DeploymentTargets:Bootstrap:WorkloadConfiguration:Ingress:ClusterIssuerName " +
                    "must be a valid Kubernetes resource name.");
            }
            if (ingress?.PublicBaseDomain is not null &&
                !DeploymentTargetIngressConfiguration.IsValidPublicBaseDomain(ingress.PublicBaseDomain))
            {
                failures.Add(
                    "DeploymentTargets:Bootstrap:WorkloadConfiguration:Ingress:PublicBaseDomain " +
                    "must be a valid ASCII DNS name.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
