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
    DateTime UpdatedAtUtc)
{
    public DeploymentTargetNetworkPolicyDto NetworkPolicy { get; init; } = DeploymentTargetNetworkPolicyDto.Unknown;
}

/// <summary>
/// Deliberately exposes only the connection discriminator and revision. It must not grow fields
/// containing kubeconfig, service-account token, certificate data, or an encrypted envelope.
/// </summary>
public sealed record DeploymentTargetConnectionDto(
    string Kind,
    long Version);

public sealed record DeploymentTargetHealthDto(
    string Status,
    DateTime? LastCheckedAtUtc,
    DateTime? LastHealthyAtUtc,
    string? FailureCode,
    string? FailureMessage);

public sealed record DeploymentTargetCapabilitiesDto(
    bool IsKnown,
    DateTime? ObservedAtUtc,
    string? KubernetesVersion,
    DeploymentTargetNamespaceCapabilitiesDto Namespace);

public sealed record DeploymentTargetNamespaceCapabilitiesDto(
    bool CanGet,
    bool CanCreate,
    bool CanUpdate,
    bool CanPatch,
    bool CanDelete);

public sealed record DeploymentTargetWorkloadConfigurationDto(
    long Revision,
    DeploymentTargetIngressConfigurationDto? Ingress);

public sealed record DeploymentTargetIngressConfigurationDto(
    string IngressClassName,
    string ClusterIssuerName,
    string? PublicBaseDomain = null);

public static class DeploymentTargetDtoExtensions
{
    public static DeploymentTargetDto ToDto(this DeploymentTarget target, bool isDefault = false) =>
        new(
            target.Id,
            target.DisplayName,
            target.ClusterIdentity,
            new DeploymentTargetConnectionDto(
                target.Connection.Kind.ToString(),
                target.Connection.Version),
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
                    : null),
            target.Revision,
            target.Enabled,
            target.AcceptsWorkloads,
            target.DeletionRequestedAtUtc,
            isDefault,
            target.CreatedAtUtc,
            target.UpdatedAtUtc)
        {
            NetworkPolicy = target.ToNetworkPolicyDto()
        };

    public static DeploymentTargetLookupDto ToLookupDto(this DeploymentTarget target) =>
        new(
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
                    : null),
            target.Revision,
            target.Enabled,
            target.AcceptsWorkloads,
            target.DeletionRequestedAtUtc is not null,
            target.Health.Status.ToString())
        {
            NetworkPolicy = target.ToNetworkPolicyDto()
        };

    public static AvailableDeploymentTargetDto ToAvailableDto(this DeploymentTarget target) =>
        new(
            target.Id,
            target.DisplayName,
            target.WorkloadConfiguration.Revision,
            target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null,
            target.WorkloadConfiguration.Ingress?.PublicBaseDomain)
        {
            NetworkPolicy = target.ToNetworkPolicyDto()
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
            _ => throw new InvalidOperationException("Unknown enforcement observation.")
        };
        return new(
            enforcement,
            enforcement == NetworkPolicyEnforcementStatus.Supported
                ? NetworkPolicyReadiness.EnforcementAttested
                : evidence.AllowIsolationNotEnforced
                    ? NetworkPolicyReadiness.IsolationNotEnforced
                    : NetworkPolicyReadiness.EnforcementRequired,
            enforcement switch
            {
                NetworkPolicyEnforcementStatus.Supported => NetworkPolicyReason.OperatorAttestedSupported,
                NetworkPolicyEnforcementStatus.Unsupported => NetworkPolicyReason.OperatorAttestedUnsupported,
                _ => NetworkPolicyReason.EnforcementNotEstablished
            },
            evidence.LastCheckedAtUtc,
            NetworkPolicyEvidenceSource.OperatorAttestation,
            evidence.AllowIsolationNotEnforced);
    }

    private static DeploymentTargetCapabilitiesDto ToDto(
        this DeploymentTargetCapabilities capabilities) =>
        new(
            capabilities.IsKnown,
            capabilities.ObservedAtUtc,
            capabilities.KubernetesVersion,
            new DeploymentTargetNamespaceCapabilitiesDto(
                capabilities.NamespaceCapabilities.CanGet,
                capabilities.NamespaceCapabilities.CanCreate,
                capabilities.NamespaceCapabilities.CanUpdate,
                capabilities.NamespaceCapabilities.CanPatch,
                capabilities.NamespaceCapabilities.CanDelete));
}
