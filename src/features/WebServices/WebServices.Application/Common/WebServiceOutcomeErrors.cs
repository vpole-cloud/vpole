// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Configuration;

namespace Pole.Features.WebServices.Application.Common;

public sealed record WebServiceNotFound(
    string WebServiceId
);

public sealed record WebServiceUpdateUnavailable(
    string WebServiceId
);

public sealed record WebServiceHasNoActiveDeployments(
    string WebServiceId
);

public sealed record WebServiceReleaseNotFound(
    string WebServiceId,
    long ResourceRevision
);

public sealed record WebServiceRollbackRevisionNotEligible(
    string WebServiceId,
    long TargetRevision
);

public sealed record WebServiceResourceDeploymentNotFound(
    string WebServiceId,
    string ResourceDeploymentId
);

public sealed record WebServiceDeploymentAlreadyDeleting(
    string WebServiceId,
    string ResourceDeploymentId
);

public sealed record WebServiceLastDeploymentCannotBeRemoved(
    string WebServiceId
);

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
    WebServiceRollbackRevisionNotEligible,
    WebServiceHasNoActiveDeployments);

public union WebServiceDeploymentMutationStateError(
    WebServiceNotFound,
    WebServiceUpdateUnavailable,
    WebServiceResourceDeploymentNotFound);

public union DeleteWebServiceDeploymentStateError(
    WebServiceNotFound,
    WebServiceResourceDeploymentNotFound,
    WebServiceDeploymentAlreadyDeleting,
    WebServiceLastDeploymentCannotBeRemoved);

public union DeleteWebServiceStateError(WebServiceNotFound, WebServiceHasNoActiveDeployments);

public sealed record WebServiceHostnameAttachmentRemovalRequired(
    string WebServiceId,
    string BindingId
);

public union DeleteWebServiceError(
    DeleteWebServiceStateError,
    WebServiceHostnameAttachmentRemovalRequired,
    SourceBuildWebServiceHasActiveJobs,
    ResourceDeploymentMutationError,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union WebServiceImageTagAuthorizationError(WebServiceImageTagUnauthorized, WebServiceImageTagForbidden);

public union WebServiceImageTagStateError(
    WebServiceNotFound,
    WebServiceUpdateUnavailable,
    WebServiceHasNoActiveDeployments,
    WebServiceImageTagAuthorizationError);

public union UpdateWebServiceError(
    WebServiceLifecycleMutationStateError,
    DeploymentTargetPublicIngressAdmissionError,
    GeneratedDomainZoneError,
    GeneratedHostnameBindingError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceConnectionConflict,
    WebServiceDependencyUnavailable,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union RedeployWebServiceError(
    WebServiceLifecycleMutationStateError,
    ResourceDeploymentMutationError,
    WebServiceConnectionConflict,
    WebServiceDependencyUnavailable,
    WebServiceUpdateConflict);

public union RollbackWebServiceError(
    RollbackWebServiceStateError,
    ResourceDeploymentMutationError,
    WebServiceConnectionConflict,
    WebServiceDependencyUnavailable,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union UpdateWebServiceImageTagError(
    WebServiceImageTagStateError,
    ResourceDeploymentMutationError,
    WebServiceConnectionConflict,
    WebServiceDependencyUnavailable,
    WebServiceUpdateConflict);

public union UpdateWebServiceDeploymentError(
    WebServiceDeploymentMutationStateError,
    ComputeProfileResolutionError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceConnectionConflict,
    WebServiceDependencyUnavailable,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union DeleteWebServiceDeploymentError(
    DeleteWebServiceDeploymentStateError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceConnectionConflict,
    WebServiceDependencyUnavailable,
    WebServiceUpdateConflict,
    QuotaExecutionError);

public union RetryWebServiceDeploymentError(
    WebServiceDeploymentMutationStateError,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceConnectionConflict,
    WebServiceDependencyUnavailable,
    WebServiceUpdateConflict);

public union WebServiceDeploymentHostnameError(
    WebServiceDeploymentMutationStateError,
    WebServiceHostnameAttachmentUnavailable,
    CustomHostnameBindingError,
    CustomHostnameBindingGitOwned,
    ResourceDeploymentMutationError,
    AdvanceResourceDeploymentRevisionError,
    WebServiceConnectionConflict,
    WebServiceDependencyUnavailable,
    WebServiceUpdateConflict);

public sealed record WebServiceHostnameAttachmentUnavailable(
    string WebServiceId
);
