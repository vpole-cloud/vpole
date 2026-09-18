using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Mutations;

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
    SourceBuildWebServiceHasActiveJobs,
    ResourceDeploymentMutationError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

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

public union RedeployWebServiceError(
    WebServiceLifecycleMutationStateError,
    ResourceDeploymentMutationError,
    WebServiceUpdateConflict);

public union RollbackWebServiceError(
    RollbackWebServiceStateError,
    ResourceDeploymentMutationError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union UpdateWebServiceImageTagError(
    WebServiceImageTagStateError,
    ResourceDeploymentMutationError,
    WebServiceUpdateConflict);

public union UpdateWebServiceDeploymentError(
    WebServiceDeploymentMutationStateError,
    ComputeProfileResolutionError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union DeleteWebServiceDeploymentError(
    DeleteWebServiceDeploymentStateError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union RetryWebServiceDeploymentError(
    WebServiceDeploymentMutationStateError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceUpdateConflict);

public union WebServiceDeploymentHostnameError(
    WebServiceDeploymentMutationStateError,
    CustomHostnameBindingError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceUpdateConflict);
