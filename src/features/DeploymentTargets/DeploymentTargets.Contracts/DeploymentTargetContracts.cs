using k8s;
using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.DeploymentTargets.Contracts;

public interface IKubernetesClientProvider
{
    Task<IKubernetes> GetAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitional single-cluster path for resources that do not carry placement yet.
    /// New placement-aware code should always call <see cref="GetAsync"/> with its persisted target ID.
    /// </summary>
    Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default);
}

public enum DeploymentTargetUnavailableReason
{
    NotFound,
    Disabled,
    DefaultNotConfigured,
    ConnectionUnavailable
}

/// <summary>
/// A deliberately sanitized failure raised when a target client cannot be supplied.
/// It must never contain connection material or a provider exception message.
/// </summary>
public sealed class DeploymentTargetUnavailableException : Exception
{
    public DeploymentTargetUnavailableException(
        string? deploymentTargetId,
        DeploymentTargetUnavailableReason reason)
        : base(CreateMessage(deploymentTargetId, reason))
    {
        DeploymentTargetId = string.IsNullOrWhiteSpace(deploymentTargetId)
            ? null
            : deploymentTargetId.Trim();
        Reason = reason;
    }

    public string? DeploymentTargetId { get; }
    public DeploymentTargetUnavailableReason Reason { get; }

    private static string CreateMessage(
        string? deploymentTargetId,
        DeploymentTargetUnavailableReason reason) =>
        string.IsNullOrWhiteSpace(deploymentTargetId)
            ? $"The default deployment target is unavailable ({reason})."
            : $"Deployment target '{deploymentTargetId.Trim()}' is unavailable ({reason}).";
}

public sealed record DeploymentTargetLookupDto(
    string Id,
    string DisplayName,
    string ClusterIdentity,
    string ConnectionKind,
    long ConnectionVersion,
    DeploymentTargetWorkloadConfigurationLookupDto WorkloadConfiguration,
    long Revision,
    bool Enabled,
    bool AcceptsWorkloads,
    bool DeletionRequested,
    string HealthStatus)
{
    public DeploymentTargetNetworkPolicyDto NetworkPolicy { get; init; } = DeploymentTargetNetworkPolicyDto.Unknown;
}

public sealed record DeploymentTargetWorkloadConfigurationLookupDto(
    long Revision,
    DeploymentTargetIngressConfigurationLookupDto? Ingress);

public sealed record DeploymentTargetIngressConfigurationLookupDto(
    string IngressClassName,
    string ClusterIssuerName,
    string? PublicBaseDomain = null);

[Flags]
public enum DeploymentTargetRequiredCapabilities
{
    None = 0,
    PublicIngress = 1,
    FunctionRuntimeConnectivity = 2
}

/// <summary>
/// Describes a placement-admission decision. Targets are currently platform-shared, but tenant
/// and resource context are deliberately carried at this boundary so tenant-owned and allowlisted
/// targets can be introduced without changing callers again.
/// </summary>
public sealed record DeploymentTargetAdmissionRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string? IsolationScopeId,
    string? ExplicitDeploymentTargetId,
    DeploymentTargetRequiredCapabilities RequiredCapabilities);

/// <summary>
/// Safe target information suitable for a tenant placement picker. It intentionally excludes
/// cluster identity, credentials, connection state, and provider diagnostics.
/// </summary>
public sealed record AvailableDeploymentTargetDto(
    string Id,
    string DisplayName,
    long WorkloadConfigurationRevision,
    bool HasPublicIngress,
    string? PublicBaseDomain)
{
    public DeploymentTargetNetworkPolicyDto NetworkPolicy { get; init; } = DeploymentTargetNetworkPolicyDto.Unknown;
}

/// <summary>
/// Immutable ingress configuration resolved for admission. Consumers snapshot this value instead
/// of reloading mutable target configuration while creating their own aggregate.
/// </summary>
public sealed record DeploymentTargetPublicIngressDto(
    string DeploymentTargetId,
    long WorkloadConfigurationRevision,
    string IngressClassName,
    string ClusterIssuerName,
    string PublicBaseDomain);

public sealed record DeploymentTargetNotFound(string DeploymentTargetId);

public sealed record DeploymentTargetDisabled(string DeploymentTargetId);

public sealed record DeploymentTargetDeletionRequested(string DeploymentTargetId);

public sealed record DeploymentTargetNewWorkloadsDisabled(string DeploymentTargetId);

public union DeploymentTargetWorkloadAdmissionError(
    DeploymentTargetNotFound,
    DeploymentTargetDisabled,
    DeploymentTargetDeletionRequested,
    DeploymentTargetNewWorkloadsDisabled);

public union AdmitDeploymentTargetForWorkloadOutcome(
    DeploymentTargetLookupDto,
    DeploymentTargetWorkloadAdmissionError);

public delegate Task<AdmitDeploymentTargetForWorkloadOutcome> AdmitDeploymentTargetForWorkload(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

public sealed record NoDeploymentTargetAcceptsWorkloads;

public sealed record DeploymentTargetRequiredCapabilitiesUnavailable(
    string DeploymentTargetId,
    DeploymentTargetRequiredCapabilities RequiredCapabilities);

public sealed record DeploymentTargetWorkloadSelectionRequired(
    IReadOnlyList<string> EligibleDeploymentTargetIds);

/// <summary>
/// Contextual admission for a new workload placement. The provider owns target
/// eligibility and unambiguous selection; consumers own only their resource
/// specific snapshot mapping.
/// </summary>
public union DeploymentTargetNewWorkloadAdmissionError(
    DeploymentTargetWorkloadAdmissionError,
    NoDeploymentTargetAcceptsWorkloads,
    DeploymentTargetRequiredCapabilitiesUnavailable,
    DeploymentTargetWorkloadSelectionRequired);

public union AdmitDeploymentTargetForNewWorkloadOutcome(
    DeploymentTargetLookupDto,
    DeploymentTargetNewWorkloadAdmissionError);

public delegate Task<AdmitDeploymentTargetForNewWorkloadOutcome> AdmitDeploymentTargetForNewWorkload(
    DeploymentTargetAdmissionRequest request,
    CancellationToken cancellationToken = default);

public sealed record NoDeploymentTargetAcceptsPublicWorkloads;

public sealed record DeploymentTargetSelectionRequired(
    IReadOnlyList<string> EligibleDeploymentTargetIds);

public sealed record DeploymentTargetPublicIngressUnavailable(
    string DeploymentTargetId);

public union DeploymentTargetPublicIngressAdmissionResult(
    DeploymentTargetPublicIngressDto,
    DeploymentTargetNotFound,
    DeploymentTargetDisabled,
    DeploymentTargetDeletionRequested,
    DeploymentTargetNewWorkloadsDisabled,
    NoDeploymentTargetAcceptsPublicWorkloads,
    DeploymentTargetSelectionRequired,
    DeploymentTargetPublicIngressUnavailable);

public union DeploymentTargetPublicIngressAdmissionError(
    DeploymentTargetNotFound,
    DeploymentTargetDisabled,
    DeploymentTargetDeletionRequested,
    DeploymentTargetNewWorkloadsDisabled,
    NoDeploymentTargetAcceptsPublicWorkloads,
    DeploymentTargetSelectionRequired,
    DeploymentTargetPublicIngressUnavailable);

public union DeploymentTargetDomainReservationAdmissionError(
    DeploymentTargetNotFound,
    DeploymentTargetDisabled,
    DeploymentTargetDeletionRequested,
    DeploymentTargetNewWorkloadsDisabled,
    DeploymentTargetPublicIngressUnavailable);

/// <summary>
/// Provider-owned admission contract for consumers that require a stable public-ingress snapshot.
/// Expected admission outcomes are represented by the closed result union; infrastructure failures
/// remain exceptions.
/// </summary>
public delegate Task<DeploymentTargetPublicIngressAdmissionResult>
    AdmitDeploymentTargetPublicIngressForNewWorkload(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default);

/// <summary>
/// Lists every target that is currently eligible for a public workload placement.
/// Consumers own any request-specific selection rule; this provider owns eligibility
/// and the immutable ingress configuration snapshot.
/// </summary>
public delegate Task<IReadOnlyList<DeploymentTargetPublicIngressDto>>
    ListEligiblePublicIngressDeploymentTargetsForNewWorkload(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default);

/// <summary>
/// Resolves public ingress for a tenant domain reservation without forcing the domain feature to
/// translate provider-owned expected errors into shared application failures.
/// </summary>
public union AdmitDeploymentTargetPublicIngressForDomainReservationOutcome(
    DeploymentTargetPublicIngressDto,
    DeploymentTargetDomainReservationAdmissionError);

public delegate Task<AdmitDeploymentTargetPublicIngressForDomainReservationOutcome>
    AdmitDeploymentTargetPublicIngressForDomainReservation(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default);

/// <summary>
/// Read-only target lookup errors. These deliberately do not assert that a target remains
/// eligible for new work: persisted runtime cells must remain addressable while their target is
/// disabled or being deleted.
/// </summary>
public union DeploymentTargetLookupError(
    DeploymentTargetNotFound);

public union LookupDeploymentTargetOutcome(
    DeploymentTargetLookupDto,
    DeploymentTargetLookupError);

public delegate Task<LookupDeploymentTargetOutcome> LookupDeploymentTarget(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

public sealed record DefaultDeploymentTargetNotConfigured;

public union DefaultDeploymentTargetLookupError(
    DefaultDeploymentTargetNotConfigured);

public union LookupDefaultDeploymentTargetOutcome(
    DeploymentTargetLookupDto,
    DefaultDeploymentTargetLookupError);

public delegate Task<LookupDefaultDeploymentTargetOutcome> LookupDefaultDeploymentTarget(
    CancellationToken cancellationToken = default);

/// <summary>
/// Lists targets that currently accept new workloads. Consumers that need an unambiguous choice
/// must make that choice explicit or call one of the admission capabilities instead.
/// </summary>
public delegate Task<IReadOnlyList<DeploymentTargetLookupDto>>
    ListEligibleDeploymentTargetsForNewWorkload(
        CancellationToken cancellationToken = default);

public union DefaultDeploymentTargetWorkloadAdmissionError(
    DefaultDeploymentTargetNotConfigured,
    DeploymentTargetDisabled,
    DeploymentTargetDeletionRequested,
    DeploymentTargetNewWorkloadsDisabled);

/// <summary>
/// Admits the configured default target for a new workload. This is intentionally distinct from
/// contextual placement selection: a caller that wants an automatic choice among all targets uses
/// <see cref="AdmitDeploymentTargetForNewWorkload"/> instead.
/// </summary>
public union AdmitDefaultDeploymentTargetForNewWorkloadOutcome(
    DeploymentTargetLookupDto,
    DefaultDeploymentTargetWorkloadAdmissionError);

public delegate Task<AdmitDefaultDeploymentTargetForNewWorkloadOutcome>
    AdmitDefaultDeploymentTargetForNewWorkload(
        CancellationToken cancellationToken = default);

public sealed record DeploymentTargetReferenceOwner(
    string Type,
    string Id);

public sealed record DeploymentTargetReferenceChangeRejected(
    DeploymentTargetReferenceOwner Owner,
    IReadOnlyList<string> DeploymentTargetIds);

public sealed record DeploymentTargetReferencesChanged;
public union ApplyDeploymentTargetReferenceChangesOutcome(
    DeploymentTargetReferencesChanged,
    DeploymentTargetReferenceChangeRejected);

public delegate Task<ApplyDeploymentTargetReferenceChangesOutcome>
    ApplyDeploymentTargetReferenceChanges(
        DeploymentTargetReferenceOwner owner,
        IReadOnlyCollection<string> addedDeploymentTargetIds,
        IReadOnlyCollection<string> removedDeploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

/// <summary>
/// Stable owner discriminators used by target-owned reference fencing. They are deliberately
/// contracts rather than feature-domain types so a target can guard domain-zone references
/// without taking a dependency back on its provider.
/// </summary>
public static class DeploymentTargetReferenceOwnerTypes
{
    public const string DomainZone = "domain-zone";
    public const string ComputeProfile = "compute-profile";
}

/// <summary>
/// Owns the central registry and write fence for durable deployment-target references. Resource
/// owners must register or unregister their reference inside the same Mongo transaction as their
/// own mutation. Calls with a session that has no active transaction fail fast.
/// </summary>
public sealed record DeploymentTargetWorkloadConfigurationFenceRequest(
    string DeploymentTargetId,
    long ExpectedWorkloadConfigurationRevision);

public sealed record DeploymentTargetWorkloadConfigurationFenceRejected(
    IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> Requests);

public sealed record DeploymentTargetWorkloadConfigurationsFenced;
public union FenceDeploymentTargetWorkloadConfigurationsOutcome(
    DeploymentTargetWorkloadConfigurationsFenced,
    DeploymentTargetWorkloadConfigurationFenceRejected);

public delegate Task<FenceDeploymentTargetWorkloadConfigurationsOutcome>
    FenceDeploymentTargetWorkloadConfigurations(
        IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
