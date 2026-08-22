using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Persistence;

namespace Pole.Features.WebServices.Application.Common;

public sealed record WebServiceNotFound(string WebServiceId);

public sealed record WebServiceUpdateUnavailable(string WebServiceId);

public sealed record WebServiceHasNoActiveDeployments(string WebServiceId);

public sealed record WebServiceReleaseNotFound(
    string WebServiceId,
    long ResourceRevision);

public sealed record WebServiceResourceDeploymentNotFound(
    string WebServiceId,
    string ResourceDeploymentId);

public sealed record WebServiceDeploymentAlreadyDeleting(
    string WebServiceId,
    string ResourceDeploymentId);

public sealed record WebServiceLastDeploymentCannotBeRemoved(string WebServiceId);

public sealed record WebServiceImageTagUnauthorized;

public sealed record WebServiceImageTagForbidden;

public union WebServiceLifecycleMutationStateError(
    WebServiceNotFound,
    WebServiceUpdateUnavailable,
    WebServiceHasNoActiveDeployments);

public union RollbackWebServiceStateError(
    WebServiceNotFound,
    WebServiceUpdateUnavailable,
    WebServiceReleaseNotFound,
    WebServiceHasNoActiveDeployments);

public union WebServiceDeploymentMutationStateError(
    WebServiceNotFound,
    WebServiceResourceDeploymentNotFound);

public union DeleteWebServiceDeploymentStateError(
    WebServiceNotFound,
    WebServiceResourceDeploymentNotFound,
    WebServiceDeploymentAlreadyDeleting,
    WebServiceLastDeploymentCannotBeRemoved);

public union DeleteWebServiceStateError(
    WebServiceNotFound,
    WebServiceHasNoActiveDeployments);

public union DeleteWebServiceError(
    DeleteWebServiceStateError,
    SourceBuildWebServiceConfigurationExists,
    ResourceDeploymentMutationError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union DeleteWebServiceOutcome(
    DeleteWebServiceResponse,
    DeleteWebServiceError);

public union WebServiceImageTagAuthorizationError(
    WebServiceImageTagUnauthorized,
    WebServiceImageTagForbidden);

public union WebServiceImageTagStateError(
    WebServiceNotFound,
    WebServiceUpdateUnavailable,
    WebServiceHasNoActiveDeployments,
    WebServiceImageTagAuthorizationError);

public union UpdateWebServiceError(
    WebServiceLifecycleMutationStateError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union UpdateWebServiceOutcome(
    UpdateWebServiceResponse,
    UpdateWebServiceError);

public union RedeployWebServiceError(
    WebServiceLifecycleMutationStateError,
    ResourceDeploymentMutationError,
    WebServiceUpdateConflict);

public union RedeployWebServiceOutcome(
    RedeployWebServiceResponse,
    RedeployWebServiceError);

public union RollbackWebServiceError(
    RollbackWebServiceStateError,
    ResourceDeploymentMutationError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union RollbackWebServiceOutcome(
    RollbackWebServiceResponse,
    RollbackWebServiceError);

public union UpdateWebServiceImageTagError(
    WebServiceImageTagStateError,
    ResourceDeploymentMutationError,
    WebServiceUpdateConflict);

public union UpdateWebServiceImageTagOutcome(
    UpdateWebServiceImageTagResponse,
    UpdateWebServiceImageTagError);

public union UpdateWebServiceDeploymentError(
    WebServiceDeploymentMutationStateError,
    ComputeProfileResolutionError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union UpdateWebServiceDeploymentOutcome(
    PlacementSummaryDto,
    UpdateWebServiceDeploymentError);

public union DeleteWebServiceDeploymentError(
    DeleteWebServiceDeploymentStateError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union DeleteWebServiceDeploymentOutcome(
    PlacementSummaryDto,
    DeleteWebServiceDeploymentError);

public union RetryWebServiceDeploymentError(
    WebServiceDeploymentMutationStateError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceUpdateConflict);

public union RetryWebServiceDeploymentOutcome(
    PlacementSummaryDto,
    RetryWebServiceDeploymentError);

public union WebServiceDeploymentHostnameError(
    WebServiceDeploymentMutationStateError,
    CustomHostnameBindingError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceUpdateConflict);

public union WebServiceDeploymentHostnameOutcome(
    PlacementSummaryDto,
    WebServiceDeploymentHostnameError);
