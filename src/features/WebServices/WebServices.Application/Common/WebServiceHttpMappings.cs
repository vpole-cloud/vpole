using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Contracts.HttpMapping;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Contracts.HttpMapping;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Contracts.HttpMapping;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Contracts.HttpMapping;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Contracts.HttpMapping;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Contracts.HttpMapping;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Deployments;
using Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;
using Pole.Features.WebServices.Application.Endpoints.Releases;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Tokens;

namespace Pole.Features.WebServices.Application.Common;

public static class WebServiceHttpMappings
{
    public static HttpProblem MapError(WebServiceNotFound error) => MapWebServiceNotFound(error);

    public static HttpProblem MapError(WebServiceDeploymentTokenNotFound error) =>
        MapWebServiceDeploymentTokenNotFound(error);

    public static HttpProblem MapError(GetWebServiceDeploymentHostnamesError error) =>
        MapGetDeploymentHostnamesError(error);

    public static HttpProblem MapError(WebServiceConnectionConflict error) => Problem(
        HttpStatusCode.Conflict,
        "web-services.connection-conflict",
        "Web service change would break a resource connection",
        ("webServiceId", error.WebServiceId),
        ("connectionId", error.ConnectionId));

    public static HttpProblem MapError(WebServiceDependencyUnavailable error) => Problem(
        HttpStatusCode.Conflict,
        "web-services.dependency-unavailable",
        "A configured dependency is unavailable",
        ("reasonCode", WebServiceDependencyDiagnostics.Code(error)));

    public static HttpProblem MapError(WebServiceUpdateConflict error) => error.Reason switch
    {
        WebServiceUpdateConflictReason.ExpectedRevisionConflict => Problem(HttpStatusCode.Conflict,
            "web-services.update.concurrent-change", "Web service changed concurrently", ("webServiceId", error.WebServiceId)),
        WebServiceUpdateConflictReason.GitOwnedConfiguration => Problem(HttpStatusCode.Conflict,
            "web-services.git-owned", "Edit the desired configuration in its managing Git source", ("webServiceId", error.WebServiceId)),
        _ => throw new ArgumentOutOfRangeException(nameof(error.Reason))
    };

    public static HttpProblem MapError(CreateWebServiceError error) => error switch
    {
        WebServiceNameAlreadyExists => Problem(HttpStatusCode.Conflict, "web-services.name-already-exists", "Web service name already exists"),
        ResourceGroupOwnershipError ownership => ResourceGroupContractHttpMappings.MapOwnershipError(ownership),
        CreateWebServicePlacementError placement => MapCreatePlacementError(placement),
        ComputeProfileResolutionError compute => ComputeProfileContractHttpMappings.MapResolutionError(compute),
        GeneratedDomainZoneError zone => DomainContractHttpMappings.MapGeneratedZoneError(zone),
        GeneratedHostnameBindingError binding => DomainContractHttpMappings.MapGeneratedBindingError(binding),
        CreateResourceDeploymentError deployment => ResourceDeploymentContractHttpMappings.MapDeploymentError(deployment),
        ResourceRegistrationMutationError registration => ResourceGroupContractHttpMappings.MapRegistrationMutationError(registration),
        QuotaExecutionError quota => QuotaContractHttpMappings.MapExecutionError(quota)
    };

    public static HttpProblem MapError(AddWebServiceDeploymentError error) => error switch
    {
        AddWebServiceDeploymentStateError state => MapStateError(state),
        DeploymentTargetPublicIngressAdmissionError target => DeploymentTargetContractHttpMappings.MapPublicIngressAdmissionError(target),
        DeploymentTargetNewWorkloadAdmissionError target => DeploymentTargetContractHttpMappings.MapNewWorkloadAdmissionError(target),
        ComputeProfileResolutionError compute => ComputeProfileContractHttpMappings.MapResolutionError(compute),
        GeneratedDomainZoneError zone => DomainContractHttpMappings.MapGeneratedZoneError(zone),
        GeneratedHostnameBindingError binding => DomainContractHttpMappings.MapGeneratedBindingError(binding),
        CreateResourceDeploymentError deployment => ResourceDeploymentContractHttpMappings.MapDeploymentError(deployment),
        AdvanceResourceDeploymentRevisionError revision => ResourceDeploymentContractHttpMappings.MapRevisionError(revision),
        WebServiceConnectionConflict conflict => MapError(conflict),
        WebServiceDependencyUnavailable unavailable => MapError(unavailable),
        WebServiceUpdateConflict conflict => MapError(conflict),
        QuotaExecutionError quota => QuotaContractHttpMappings.MapExecutionError(quota)
    };

    public static HttpProblem MapError(UpdateWebServiceError error) => error switch
    {
        WebServiceLifecycleMutationStateError state => MapLifecycleMutationStateError(state),
        DeploymentTargetPublicIngressAdmissionError target => DeploymentTargetContractHttpMappings.MapPublicIngressAdmissionError(target),
        GeneratedDomainZoneError zone => DomainContractHttpMappings.MapGeneratedZoneError(zone),
        GeneratedHostnameBindingError binding => DomainContractHttpMappings.MapGeneratedBindingError(binding),
        ResourceDeploymentMutationError deployment => ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
        AdvanceResourceDeploymentRevisionError revision => ResourceDeploymentContractHttpMappings.MapRevisionError(revision),
        WebServiceConnectionConflict conflict => MapError(conflict),
        WebServiceDependencyUnavailable unavailable => MapError(unavailable),
        WebServiceUpdateConflict conflict => MapError(conflict),
        QuotaExecutionError quota => QuotaContractHttpMappings.MapExecutionError(quota)
    };

    public static HttpProblem MapError(DeleteWebServiceError error) => error switch
    {
        DeleteWebServiceStateError state => MapDeleteStateError(state),
        SourceBuildWebServiceHasActiveJobs jobs => MapSourceBuildWebServiceHasActiveJobs(jobs),
        ResourceDeploymentMutationError deployment => ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
        WebServiceUpdateConflict conflict => MapError(conflict),
        QuotaExecutionError quota => QuotaContractHttpMappings.MapExecutionError(quota)
    };

    public static HttpProblem MapError(RedeployWebServiceError error) => error switch
    {
        WebServiceLifecycleMutationStateError state => MapLifecycleMutationStateError(state),
        ResourceDeploymentMutationError deployment => ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
        WebServiceConnectionConflict conflict => MapError(conflict),
        WebServiceDependencyUnavailable unavailable => MapError(unavailable),
        WebServiceUpdateConflict conflict => MapError(conflict)
    };

    public static HttpProblem MapError(RollbackWebServiceError error) => error switch
    {
        RollbackWebServiceStateError state => MapRollbackStateError(state),
        ResourceDeploymentMutationError deployment => ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
        WebServiceConnectionConflict conflict => MapError(conflict),
        WebServiceDependencyUnavailable unavailable => MapError(unavailable),
        WebServiceUpdateConflict conflict => MapError(conflict),
        QuotaExecutionError quota => QuotaContractHttpMappings.MapExecutionError(quota)
    };

    public static HttpProblem MapRollbackWebServicePublicSpecIncompatible(
        RollbackWebServicePublicSpecIncompatible incompatible) =>
        Problem(
            HttpStatusCode.UnprocessableEntity,
            "web-services.rollback.public-spec-incompatible",
            "The historical web service specification cannot serve public ingress",
            ("webServiceId", incompatible.WebServiceId),
            ("targetRevision", incompatible.TargetRevision));

    public static HttpProblem MapError(UpdateWebServiceImageTagError error) => error switch
    {
        WebServiceImageTagStateError state => MapImageTagStateError(state),
        ResourceDeploymentMutationError deployment => ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
        WebServiceConnectionConflict conflict => MapError(conflict),
        WebServiceDependencyUnavailable unavailable => MapError(unavailable),
        WebServiceUpdateConflict conflict => MapError(conflict)
    };

    public static HttpProblem MapError(UpdateWebServiceDeploymentError error) => error switch
    {
        WebServiceDeploymentMutationStateError state => MapDeploymentMutationStateError(state),
        ComputeProfileResolutionError compute => ComputeProfileContractHttpMappings.MapResolutionError(compute),
        ResourceDeploymentMutationError deployment => ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
        AdvanceResourceDeploymentRevisionError revision => ResourceDeploymentContractHttpMappings.MapRevisionError(revision),
        WebServiceConnectionConflict conflict => MapError(conflict),
        WebServiceDependencyUnavailable unavailable => MapError(unavailable),
        WebServiceUpdateConflict conflict => MapError(conflict),
        QuotaExecutionError quota => QuotaContractHttpMappings.MapExecutionError(quota)
    };

    public static HttpProblem MapError(DeleteWebServiceDeploymentError error) => error switch
    {
        DeleteWebServiceDeploymentStateError state => MapDeleteDeploymentStateError(state),
        ResourceDeploymentMutationError deployment => ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
        AdvanceResourceDeploymentRevisionError revision => ResourceDeploymentContractHttpMappings.MapRevisionError(revision),
        WebServiceConnectionConflict conflict => MapError(conflict),
        WebServiceDependencyUnavailable unavailable => MapError(unavailable),
        WebServiceUpdateConflict conflict => MapError(conflict),
        QuotaExecutionError quota => QuotaContractHttpMappings.MapExecutionError(quota)
    };

    public static HttpProblem MapError(RetryWebServiceDeploymentError error) => error switch
    {
        WebServiceDeploymentMutationStateError state => MapDeploymentMutationStateError(state),
        ResourceDeploymentMutationError deployment => ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
        AdvanceResourceDeploymentRevisionError revision => ResourceDeploymentContractHttpMappings.MapRevisionError(revision),
        WebServiceConnectionConflict conflict => MapError(conflict),
        WebServiceDependencyUnavailable unavailable => MapError(unavailable),
        WebServiceUpdateConflict conflict => MapError(conflict)
    };

    public static HttpProblem MapError(WebServiceDeploymentHostnameError error) => error switch
    {
        WebServiceDeploymentMutationStateError state => MapDeploymentMutationStateError(state),
        WebServiceHostnameAttachmentUnavailable unavailable => Problem(
            HttpStatusCode.Conflict,
            "web-services.private-hostname-unavailable",
            "Custom hostnames require public web-service exposure",
            ("webServiceId", unavailable.WebServiceId)),
        CustomHostnameBindingError hostname => DomainContractHttpMappings.MapCustomHostnameBindingError(hostname),
        CustomHostnameBindingGitOwned owned => DomainContractHttpMappings.MapCustomHostnameBindingGitOwned(owned),
        ResourceDeploymentMutationError deployment => ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
        AdvanceResourceDeploymentRevisionError revision => ResourceDeploymentContractHttpMappings.MapRevisionError(revision),
        WebServiceConnectionConflict conflict => MapError(conflict),
        WebServiceDependencyUnavailable unavailable => MapError(unavailable),
        WebServiceUpdateConflict conflict => MapError(conflict)
    };

    private static HttpProblem MapLifecycleMutationStateError(
        WebServiceLifecycleMutationStateError error) => error switch
        {
            WebServiceNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.not-found",
                "Web service not found",
                ("webServiceId", notFound.WebServiceId)),
            WebServiceUpdateUnavailable unavailable => Problem(
                HttpStatusCode.Conflict,
                "web-services.update-unavailable",
                "Web service cannot be updated in its current lifecycle",
                ("webServiceId", unavailable.WebServiceId)),
            WebServiceHasNoActiveDeployments empty => Problem(
                HttpStatusCode.Conflict,
                "web-services.no-active-deployments",
                "Web service has no active resource deployments",
                ("webServiceId", empty.WebServiceId))
        };

    private static HttpProblem MapWebServiceNotFound(WebServiceNotFound error) =>
        Problem(
            HttpStatusCode.NotFound,
            "web-services.not-found",
            "Web service not found",
            ("webServiceId", error.WebServiceId));

    private static HttpProblem MapWebServiceDeploymentTokenNotFound(
        WebServiceDeploymentTokenNotFound error) => Problem(
            HttpStatusCode.NotFound,
            "web-services.deployment-tokens.not-found",
            "Web service deployment token not found",
            ("tokenId", error.TokenId));

    private static HttpProblem MapRollbackStateError(
        RollbackWebServiceStateError error) => error switch
        {
            WebServiceNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.not-found",
                "Web service not found",
                ("webServiceId", notFound.WebServiceId)),
            WebServiceUpdateUnavailable unavailable => Problem(
                HttpStatusCode.Conflict,
                "web-services.update-unavailable",
                "Web service cannot be rolled back in its current lifecycle",
                ("webServiceId", unavailable.WebServiceId)),
            WebServiceReleaseNotFound release => Problem(
                HttpStatusCode.NotFound,
                "web-services.release-not-found",
                "Succeeded web service release was not found",
                ("webServiceId", release.WebServiceId),
                ("resourceRevision", release.ResourceRevision)),
            WebServiceRollbackRevisionNotEligible revision => Problem(
                HttpStatusCode.Conflict,
                "web-services.rollback-revision-not-eligible",
                "Rollback requires a succeeded release before the active revision",
                ("webServiceId", revision.WebServiceId),
                ("targetRevision", revision.TargetRevision)),
            WebServiceHasNoActiveDeployments empty => Problem(
                HttpStatusCode.Conflict,
                "web-services.no-active-deployments",
                "Web service has no active resource deployments",
                ("webServiceId", empty.WebServiceId))
        };

    private static HttpProblem MapDeploymentMutationStateError(
        WebServiceDeploymentMutationStateError error) => error switch
        {
            WebServiceUpdateUnavailable unavailable => Problem(
                HttpStatusCode.Conflict,
                "web-services.update-unavailable",
                "Web service cannot be updated in its current lifecycle",
                ("webServiceId", unavailable.WebServiceId)),
            WebServiceNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.not-found",
                "Web service not found",
                ("webServiceId", notFound.WebServiceId)),
            WebServiceResourceDeploymentNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.deployments.not-found",
                "Web service deployment not found",
                ("webServiceId", notFound.WebServiceId),
                ("resourceDeploymentId", notFound.ResourceDeploymentId))
        };

    private static HttpProblem MapDeleteDeploymentStateError(
        DeleteWebServiceDeploymentStateError error) => error switch
        {
            WebServiceNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.not-found",
                "Web service not found",
                ("webServiceId", notFound.WebServiceId)),
            WebServiceResourceDeploymentNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.deployments.not-found",
                "Web service deployment not found",
                ("webServiceId", notFound.WebServiceId),
                ("resourceDeploymentId", notFound.ResourceDeploymentId)),
            WebServiceDeploymentAlreadyDeleting deleting => Problem(
                HttpStatusCode.Conflict,
                "web-services.deployments.already-deleting",
                "Web service deployment is already being removed",
                ("webServiceId", deleting.WebServiceId),
                ("resourceDeploymentId", deleting.ResourceDeploymentId)),
            WebServiceLastDeploymentCannotBeRemoved last => Problem(
                HttpStatusCode.Conflict,
                "web-services.deployments.last-cannot-be-removed",
                "The last web service deployment cannot be removed",
                ("webServiceId", last.WebServiceId))
        };

    private static HttpProblem MapDeleteStateError(
        DeleteWebServiceStateError error) => error switch
        {
            WebServiceNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.not-found",
                "Web service not found",
                ("webServiceId", notFound.WebServiceId)),
            WebServiceHasNoActiveDeployments empty => Problem(
                HttpStatusCode.Conflict,
                "web-services.no-active-deployments",
                "Web service has no active resource deployments",
                ("webServiceId", empty.WebServiceId))
        };

    private static HttpProblem MapSourceBuildWebServiceHasActiveJobs(
        SourceBuildWebServiceHasActiveJobs error) => Problem(
            HttpStatusCode.Conflict,
            "source-builds.web-service-has-active-jobs",
            "Wait for active source builds to finish before deleting this web service",
            ("webServiceId", error.WebServiceId));

    private static HttpProblem MapGetDeploymentHostnamesError(
        GetWebServiceDeploymentHostnamesError error) => error switch
        {
            WebServiceNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.not-found",
                "Web service not found",
                ("webServiceId", notFound.WebServiceId)),
            WebServiceResourceDeploymentNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.deployments.not-found",
                "Web service deployment not found",
                ("webServiceId", notFound.WebServiceId),
                ("resourceDeploymentId", notFound.ResourceDeploymentId))
        };

    private static HttpProblem MapImageTagAuthorizationError(
        WebServiceImageTagAuthorizationError error) => error switch
        {
            WebServiceImageTagUnauthorized => Problem(
                HttpStatusCode.Unauthorized,
                "web-services.image-tag.unauthorized",
                "A user access token or service deployment token is required"),
            WebServiceImageTagForbidden => Problem(
                HttpStatusCode.Forbidden,
                "web-services.image-tag.forbidden",
                "The caller is not allowed to update web service image tags")
        };

    private static HttpProblem MapImageTagStateError(
        WebServiceImageTagStateError error) => error switch
        {
            WebServiceNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.not-found",
                "Web service not found",
                ("webServiceId", notFound.WebServiceId)),
            WebServiceUpdateUnavailable unavailable => Problem(
                HttpStatusCode.Conflict,
                "web-services.update-unavailable",
                "Web service cannot be updated in its current lifecycle",
                ("webServiceId", unavailable.WebServiceId)),
            WebServiceHasNoActiveDeployments empty => Problem(
                HttpStatusCode.Conflict,
                "web-services.no-active-deployments",
                "Web service has no active resource deployments",
                ("webServiceId", empty.WebServiceId)),
            WebServiceImageTagAuthorizationError authorization =>
                MapImageTagAuthorizationError(authorization)
        };

    private static HttpProblem MapStateError(
        AddWebServiceDeploymentStateError error) => error switch
        {
            WebServiceDeploymentNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "web-services.not-found",
                "Web service not found",
                ("webServiceId", notFound.WebServiceId)),
            WebServiceDeploymentUpdateUnavailable unavailable => Problem(
                HttpStatusCode.Conflict,
                "web-services.deployments.update-unavailable",
                "Web service deployments cannot be updated",
                ("webServiceId", unavailable.WebServiceId)),
            WebServiceDeploymentTargetAlreadyExists existing => Problem(
                HttpStatusCode.Conflict,
                "web-services.deployments.target-exists",
                "Web service already has a deployment on this target",
                ("webServiceId", existing.WebServiceId),
                ("deploymentTargetId", existing.DeploymentTargetId))
        };

    private static HttpProblem MapCreatePlacementError(
        CreateWebServicePlacementError error) => error switch
        {
            NoDeploymentTargetAcceptsWorkloads => Problem(
                HttpStatusCode.Conflict,
                "deployment-targets.no-workload-target",
                "No deployment target accepts web-service workloads"),
            NoDeploymentTargetAcceptsPublicWorkloads => Problem(
                HttpStatusCode.Conflict,
                "deployment-targets.no-public-workload-target",
                "No deployment target accepts public web-service workloads"),
            DeploymentTargetSelectionRequired selection => Problem(
                HttpStatusCode.UnprocessableEntity,
                "deployment-targets.selection-required",
                "A deployment target must be selected",
                ("eligibleDeploymentTargetIds", selection.EligibleDeploymentTargetIds)),
            CreateWebServiceTargetUnavailable unavailable => Problem(
                HttpStatusCode.Conflict,
                "deployment-targets.unavailable-for-web-service",
                "The selected deployment target is unavailable for this web-service placement",
                ("deploymentTargetId", unavailable.DeploymentTargetId))
        };

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title,
        params (string Name, object Value)[] extensions) =>
        new(
            (int)status,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions: extensions.ToDictionary(
                extension => extension.Name,
                extension => extension.Value));
}
