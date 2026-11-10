using System.Collections.Immutable;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed record DeploymentTargetDto(
    string Id,
    string DisplayName,
    string ClusterIdentity,
    DeploymentTargetConnectionDto Connection,
    DeploymentTargetHealthDto Health,
    DeploymentTargetCapabilitiesDto Capabilities,
    DeploymentTargetWorkloadConfigurationDto WorkloadConfiguration,
    long Revision,
    bool Enabled,
    bool AcceptsWorkloads,
    DateTime? DeletionRequestedAtUtc,
    bool IsDefault,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string TechnicalName,
    DeploymentTargetNetworkPolicyDto NetworkPolicy
);

/// <summary>
///     Deliberately exposes only the connection discriminator and revision. It must not grow fields
///     containing kubeconfig, service-account token, certificate data, or an encrypted envelope.
/// </summary>
public sealed record DeploymentTargetConnectionDto(
    string Kind,
    long Version
);

public sealed record DeploymentTargetHealthDto(
    string Status,
    DateTime? LastCheckedAtUtc,
    DateTime? LastHealthyAtUtc,
    string? FailureCode,
    string? FailureMessage
);

public sealed record DeploymentTargetCapabilitiesDto(
    bool IsKnown,
    DateTime? ObservedAtUtc,
    string? KubernetesVersion,
    DeploymentTargetNamespaceCapabilitiesDto Namespace,
    DeploymentTargetNetworkPolicyCapabilitiesDto NetworkPolicy
);

public sealed record DeploymentTargetNamespaceCapabilitiesDto(
    bool CanGet,
    bool CanCreate,
    bool CanUpdate,
    bool CanPatch,
    bool CanDelete
);

public sealed record DeploymentTargetNetworkPolicyCapabilitiesDto(
    bool CanGet,
    bool CanCreate,
    bool CanUpdate,
    bool CanPatch,
    bool CanDelete
);

public sealed record DeploymentTargetWorkloadConfigurationDto(
    long Revision,
    DeploymentTargetIngressConfigurationDto? Ingress,
    DeploymentTargetNetworkSourcesDto NetworkSources
);

public sealed record DeploymentTargetIngressConfigurationDto(
    string IngressClassName,
    string ClusterIssuerName,
    string? PublicBaseDomain = null
);

public static class DeploymentTargetDtoExtensions
{
    public static DeploymentTargetDto ToDto(this DeploymentTarget target, bool isDefault = false) => new(
        target.Id,
        target.DisplayName,
        target.ClusterIdentity,
        new DeploymentTargetConnectionDto(target.Connection.Kind.ToString(), target.Connection.Version),
        new DeploymentTargetHealthDto(
            target.Health.Status.ToString(),
            target.Health.LastCheckedAtUtc,
            target.Health.LastHealthyAtUtc,
            target.Health.FailureCode,
            target.Health.FailureMessage),
        target.Capabilities.ToDto(),
        new DeploymentTargetWorkloadConfigurationDto(
            target.WorkloadConfiguration.Revision,
            target.WorkloadConfiguration.Ingress is { } ingress
                ? new DeploymentTargetIngressConfigurationDto(
                    ingress.IngressClassName,
                    ingress.ClusterIssuerName,
                    ingress.PublicBaseDomain)
                : null,
            target.WorkloadConfiguration.NetworkSources.ToDto()),
        target.Revision,
        target.Enabled,
        target.AcceptsWorkloads,
        target.DeletionRequestedAtUtc,
        isDefault,
        target.CreatedAtUtc,
        target.UpdatedAtUtc,
        TechnicalName: target.TechnicalName,
        NetworkPolicy: target.ToNetworkPolicyDto());

    public static DeploymentTargetLookupDto ToLookupDto(this DeploymentTarget target) => new(
        target.Id,
        target.DisplayName,
        target.ClusterIdentity,
        target.Connection.Kind.ToString(),
        target.Connection.Version,
        new DeploymentTargetWorkloadConfigurationLookupDto(
            target.WorkloadConfiguration.Revision,
            target.WorkloadConfiguration.Ingress is { } ingress
                ? new DeploymentTargetIngressConfigurationLookupDto(
                    ingress.IngressClassName,
                    ingress.ClusterIssuerName,
                    ingress.PublicBaseDomain)
                : null,
            target.WorkloadConfiguration.NetworkSources.ToDto()),
        target.Revision,
        target.Enabled,
        target.AcceptsWorkloads,
        target.DeletionRequestedAtUtc is not null,
        target.Health.Status.ToString(),
        TechnicalName: target.TechnicalName,
        NetworkPolicy: target.ToNetworkPolicyDto());

    public static AvailableDeploymentTargetDto ToAvailableDto(this DeploymentTarget target) => new(
        target.Id,
        target.DisplayName,
        target.WorkloadConfiguration.Revision,
        target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null,
        target.WorkloadConfiguration.Ingress?.PublicBaseDomain,
        TechnicalName: target.TechnicalName,
        NetworkPolicy: target.ToNetworkPolicyDto());

    public static DeploymentTargetNetworkSourcesDto ToDto(this DeploymentTargetNetworkSources sources) => new(
        sources.Entries.ToImmutableDictionary(
            entry => entry.Key,
            entry => entry.Value.ToDto(),
            StringComparer.Ordinal));

    public static DeploymentTargetNetworkSourceDto ToDto(this DeploymentTargetNetworkSource source) => source switch
    {
        DeploymentTargetPodNetworkSource pod => new DeploymentTargetPodNetworkSourceDto(pod.Namespace, pod.PodLabels),
        DeploymentTargetCidrNetworkSource cidr => new DeploymentTargetCidrNetworkSourceDto(cidr.Cidr),
    };

    public static DeploymentTargetNetworkPolicyDto ToNetworkPolicyDto(this DeploymentTarget target)
    {
        var policy = target.WorkloadConfiguration.NetworkPolicy;
        if (policy is not AttestedNetworkPolicy evidence || !policy.Matches(target))
            return DeploymentTargetNetworkPolicyDto.Unknown;

        var enforcement = evidence.Enforcement switch
        {
            NetworkPolicyEnforcement.Unknown => NetworkPolicyEnforcementStatus.Unknown,
            NetworkPolicyEnforcement.Supported => NetworkPolicyEnforcementStatus.Supported,
            NetworkPolicyEnforcement.Unsupported => NetworkPolicyEnforcementStatus.Unsupported,
            _ => throw new InvalidOperationException("Unknown enforcement observation."),
        };
        return new DeploymentTargetNetworkPolicyDto(
            enforcement,
            !target.Capabilities.NetworkPolicyCapabilities.CanManage ? NetworkPolicyReadiness.EnforcementRequired :
            enforcement == NetworkPolicyEnforcementStatus.Supported ? NetworkPolicyReadiness.EnforcementAttested :
            evidence.AllowIsolationNotEnforced ? NetworkPolicyReadiness.IsolationNotEnforced :
            NetworkPolicyReadiness.EnforcementRequired,
            !target.Capabilities.NetworkPolicyCapabilities.CanManage
                ? NetworkPolicyReason.WriterPermissionsMissing
                : enforcement switch
                {
                    NetworkPolicyEnforcementStatus.Supported => NetworkPolicyReason.OperatorAttestedSupported,
                    NetworkPolicyEnforcementStatus.Unsupported => NetworkPolicyReason.OperatorAttestedUnsupported,
                    _ => NetworkPolicyReason.EnforcementNotEstablished,
                },
            evidence.LastCheckedAtUtc,
            NetworkPolicyEvidenceSource.OperatorAttestation,
            evidence.AllowIsolationNotEnforced);
    }

    private static DeploymentTargetCapabilitiesDto ToDto(this DeploymentTargetCapabilities capabilities) => new(
        capabilities.IsKnown,
        capabilities.ObservedAtUtc,
        capabilities.KubernetesVersion,
        new DeploymentTargetNamespaceCapabilitiesDto(
            capabilities.NamespaceCapabilities.CanGet,
            capabilities.NamespaceCapabilities.CanCreate,
            capabilities.NamespaceCapabilities.CanUpdate,
            capabilities.NamespaceCapabilities.CanPatch,
            capabilities.NamespaceCapabilities.CanDelete),
        new DeploymentTargetNetworkPolicyCapabilitiesDto(
            capabilities.NetworkPolicyCapabilities.CanGet,
            capabilities.NetworkPolicyCapabilities.CanCreate,
            capabilities.NetworkPolicyCapabilities.CanUpdate,
            capabilities.NetworkPolicyCapabilities.CanPatch,
            capabilities.NetworkPolicyCapabilities.CanDelete));
}
