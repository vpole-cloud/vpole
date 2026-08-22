using Pole.ResultType;

namespace Pole.Features.WebServices.Contracts;

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

public sealed record SourceBuildDeploymentResult(string ReleaseId, long Revision);

public sealed record SourceBuildWebServiceNotFound(string WebServiceId);

public sealed record SourceBuildWebServiceDeleting(string WebServiceId);

public sealed record SourceBuildWebServiceHasNoActiveDeployments(string WebServiceId);

public sealed record SourceBuildResourceDeploymentNotFound(
    string? ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentChanged(
    string? ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentCardinalityConflict;

public sealed record SourceBuildResourceDeploymentPendingDeletion(
    string? ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentPlacementUnavailable(
    string? ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentReplacementTargetMismatch(
    string? ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentIngressConfigurationChanged(
    string ResourceDeploymentId);

public sealed record SourceBuildResourceDeploymentRetryUnavailable(
    string ResourceDeploymentId);

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

public delegate Task<
    Result<SourceBuildDeploymentResult, SourceBuildWebServiceDeploymentError>>
    DeploySourceBuildWebService(
        SourceBuildDeploymentRequest request,
        CancellationToken cancellationToken = default);
