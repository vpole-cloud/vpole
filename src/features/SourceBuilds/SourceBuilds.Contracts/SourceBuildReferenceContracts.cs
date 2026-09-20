using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Contracts;

public sealed record SourceBuildWebServiceConfigurationExists(string WebServiceId);

public sealed record SourceBuildSourceRepositoryConfigurationExists(string SourceRepositoryId);

/// <summary>
/// Resolves whether the WebServices target named by a source-build
/// configuration exists for its tenant. WebServices supplies this narrow
/// contribution without making SourceBuilds depend on WebServices.Contracts.
/// </summary>
public delegate Task<bool> SourceBuildWebServiceExists(
    string tenantId,
    string webServiceId,
    CancellationToken cancellationToken = default);

/// <summary>
/// Guards a WebService deletion against source-build configuration that still
/// references it. The guard is provider-owned so consumers do not translate a
/// source-build invariant through generic application failures.
/// </summary>
public delegate Task<Result<Unit, SourceBuildWebServiceConfigurationExists>>
    EnsureNoWebServiceSourceBuildConfiguration(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default);

/// <summary>
/// Guards a source-repository deletion against source-build configuration that
/// still references it. The source-build feature owns this invariant.
/// </summary>
public delegate Task<Result<Unit, SourceBuildSourceRepositoryConfigurationExists>>
    EnsureNoSourceRepositoryBuildConfiguration(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default);

/// <summary>
/// Requests that the workload which owns a source-build target deploy the
/// produced image. The source-build feature owns this contribution boundary:
/// it records the job and needs the resulting release identifier to await
/// completion.
/// </summary>
public sealed record SourceBuildDeploymentImage(
    string RegistryHost,
    string ImageName,
    string Tag,
    string Digest,
    string PullSecretName);

public sealed record SourceBuildDeploymentRequest(
    string TenantId,
    string WebServiceId,
    string BuildJobId,
    string SourceRepositoryId,
    string CommitSha,
    string? ActorUserId,
    string? ActorEmail,
    SourceBuildDeploymentImage Image);

public sealed record SourceBuildDeploymentResult(
    string ReleaseId,
    long Revision);

public sealed record SourceBuildWebServiceNotFound(string WebServiceId);

public sealed record SourceBuildWebServiceDeleting(string WebServiceId);

public sealed record SourceBuildWebServiceHasNoActiveDeployments(string WebServiceId);

public sealed record SourceBuildResourceDeploymentNotFound(string? ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentChanged(string? ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentCardinalityConflict;

public sealed record SourceBuildResourceDeploymentPendingDeletion(string? ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentPlacementUnavailable(string? ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentReplacementTargetMismatch(string? ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentIngressConfigurationChanged(string ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentRetryUnavailable(string ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentComputeConfigurationChanged;

public union SourceBuildResourceDeploymentRejection(
    SourceBuildResourceDeploymentNotFound,
    SourceBuildResourceDeploymentChanged,
    SourceBuildResourceDeploymentCardinalityConflict,
    SourceBuildResourceDeploymentPendingDeletion,
    SourceBuildResourceDeploymentPlacementUnavailable,
    SourceBuildResourceDeploymentReplacementTargetMismatch,
    SourceBuildResourceDeploymentIngressConfigurationChanged,
    SourceBuildResourceDeploymentRetryUnavailable,
    SourceBuildResourceDeploymentComputeConfigurationChanged);

public sealed record SourceBuildWebServiceDeploymentRejected(
    string WebServiceId,
    SourceBuildResourceDeploymentRejection Reason);

public sealed record SourceBuildWebServiceUpdateConflict(string WebServiceId);

public union SourceBuildWebServiceDeploymentError(
    SourceBuildWebServiceNotFound,
    SourceBuildWebServiceDeleting,
    SourceBuildWebServiceHasNoActiveDeployments,
    SourceBuildWebServiceDeploymentRejected,
    SourceBuildWebServiceUpdateConflict);

public delegate Task<Result<SourceBuildDeploymentResult, SourceBuildWebServiceDeploymentError>>
    DeploySourceBuildWebService(
        SourceBuildDeploymentRequest request,
        CancellationToken cancellationToken = default);

/// <summary>
/// Reports the terminal state of a release created for a source build. The
/// adapter which owns the deployment-completed event maps that event into this
/// source-build contribution instead of making SourceBuilds consume a foreign
/// event contract.
/// </summary>
public sealed record SourceBuildDeploymentCompletion(
    string TenantId,
    string ReleaseId,
    string ReleaseOutcome,
    string? FailureMessage,
    DateTime CompletedAtUtc);

public delegate Task CompleteSourceBuildJobOnDeployment(
    SourceBuildDeploymentCompletion completion,
    CancellationToken cancellationToken = default);
