using k8s;
using MongoDB.Driver;
using Pole.ResultType;

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
    string ClusterIssuerName);

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
}

public sealed record DeploymentTargetReferenceOwner(string Type, string Id);

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
    Task<Result> FenceAsync(
        string deploymentTargetId,
        long expectedWorkloadConfigurationRevision,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}
