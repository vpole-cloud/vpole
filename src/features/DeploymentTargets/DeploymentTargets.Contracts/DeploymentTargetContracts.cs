using k8s;
using MongoDB.Driver;
using Pole.ResultType;
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
    string HealthStatus);

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
    PublicIngress = 1
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
    string? PublicBaseDomain);

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

public sealed record InvalidDeploymentTargetId(string DeploymentTargetId);

public sealed record DeploymentTargetNotFound(string DeploymentTargetId);

public sealed record DeploymentTargetDisabled(string DeploymentTargetId);

public sealed record DeploymentTargetDeletionRequested(string DeploymentTargetId);

public sealed record DeploymentTargetNewWorkloadsDisabled(string DeploymentTargetId);

public sealed record NoDeploymentTargetAcceptsPublicWorkloads;

public sealed record DeploymentTargetSelectionRequired(
    IReadOnlyList<string> EligibleDeploymentTargetIds);

public sealed record DeploymentTargetPublicIngressUnavailable(
    string DeploymentTargetId);

public union DeploymentTargetPublicIngressAdmissionResult(
    DeploymentTargetPublicIngressDto,
    InvalidDeploymentTargetId,
    DeploymentTargetNotFound,
    DeploymentTargetDisabled,
    DeploymentTargetDeletionRequested,
    DeploymentTargetNewWorkloadsDisabled,
    NoDeploymentTargetAcceptsPublicWorkloads,
    DeploymentTargetSelectionRequired,
    DeploymentTargetPublicIngressUnavailable);

public union DeploymentTargetPublicIngressAdmissionError(
    InvalidDeploymentTargetId,
    DeploymentTargetNotFound,
    DeploymentTargetDisabled,
    DeploymentTargetDeletionRequested,
    DeploymentTargetNewWorkloadsDisabled,
    NoDeploymentTargetAcceptsPublicWorkloads,
    DeploymentTargetSelectionRequired,
    DeploymentTargetPublicIngressUnavailable);

public union DeploymentTargetDomainReservationAdmissionError(
    InvalidDeploymentTargetId,
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
public interface IDeploymentTargetPublicIngressAdmission
{
    Task<DeploymentTargetPublicIngressAdmissionResult> AdmitForNewWorkloadAsync(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves public ingress for a tenant domain reservation without forcing the domain feature
    /// to translate provider-owned expected errors into shared application failures.
    /// </summary>
    Task<Result<
        DeploymentTargetPublicIngressDto,
        DeploymentTargetDomainReservationAdmissionError>> AdmitForDomainReservationAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default);
}

public interface IDeploymentTargetLookup
{
    Task<Result<DeploymentTargetLookupDto>> GetAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default);

    Task<Result<DeploymentTargetLookupDto>> GetDefaultAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an explicitly requested target, or the default target when the ID is omitted,
    /// and rejects targets that are disabled or do not accept new workloads.
    /// </summary>
    Task<Result<DeploymentTargetLookupDto>> ResolveForNewWorkloadAsync(
        string? deploymentTargetId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DeploymentTargetLookupDto>>> ListEligibleForNewWorkloadAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tenant-aware target admission for new placement. The simpler overloads above retain
    /// platform-shared behavior for callers that do not require contextual admission.
    /// </summary>
    Task<Result<DeploymentTargetLookupDto>> ResolveForNewWorkloadAsync(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default) =>
        ResolveForNewWorkloadAsync(request.ExplicitDeploymentTargetId, cancellationToken);

    /// <summary>
    /// Lists targets eligible for this tenant/resource placement request. Implementations must
    /// apply <see cref="DeploymentTargetRequiredCapabilities"/> before returning the list.
    /// </summary>
    Task<Result<IReadOnlyList<DeploymentTargetLookupDto>>> ListEligibleForNewWorkloadAsync(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default) =>
        ListEligibleForNewWorkloadAsync(cancellationToken);

    /// <summary>
    /// Resolves the public ingress snapshot required by an admitted placement. The caller must
    /// pass its tenant/resource/isolation context through <paramref name="request"/>.
    /// </summary>
    Task<Result<DeploymentTargetPublicIngressDto>> ResolvePublicIngressForNewWorkloadAsync(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves public ingress for reserving a tenant domain zone. Domain zones are DNS
    /// control-plane objects, not workloads, so this admission path deliberately does not invent
    /// a resource type or apply workload-type eligibility policy.
    /// </summary>
    Task<Result<DeploymentTargetPublicIngressDto>> ResolvePublicIngressForDomainReservationAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default);
}

public sealed record DeploymentTargetReferenceOwner(string Type, string Id);

/// <summary>
/// Stable owner discriminators used by target-owned reference fencing. They are deliberately
/// contracts rather than feature-domain types so a target can guard domain-zone references
/// without taking a dependency back on its provider.
/// </summary>
public static class DeploymentTargetReferenceOwnerTypes
{
    public const string DomainZone = "domain-zone";
}

/// <summary>
/// Owns the central registry and write fence for durable deployment-target references. Resource
/// owners must register or unregister their reference inside the same Mongo transaction as their
/// own mutation. Calls with a session that has no active transaction fail fast.
/// </summary>
public interface IDeploymentTargetReferenceAdmission
{
    Task<Result> RegisterAsync(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> UnregisterAsync(
        string deploymentTargetId,
        DeploymentTargetReferenceOwner owner,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> ApplyChangesAsync(
        DeploymentTargetReferenceOwner owner,
        IReadOnlyCollection<string> addedDeploymentTargetIds,
        IReadOnlyCollection<string> removedDeploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public interface IDeploymentTargetWorkloadConfigurationFence
{
    Task<Result> FenceManyAsync(
        IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> FenceAsync(
        string deploymentTargetId,
        long expectedWorkloadConfigurationRevision,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public sealed record DeploymentTargetWorkloadConfigurationFenceRequest(
    string DeploymentTargetId,
    long ExpectedWorkloadConfigurationRevision);
