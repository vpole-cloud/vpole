using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Deployments;
using Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Tokens;

namespace Pole.Features.WebServices.Application.Common;

public static class WebServiceHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<AddWebServiceDeploymentStateError>(MapStateError),
            HttpProblemMapping.For<CreateWebServicePlacementError>(MapCreatePlacementError),
            HttpProblemMapping.For<WebServiceNotFound>(MapWebServiceNotFound),
            HttpProblemMapping.For<WebServiceReleaseNotFound>(MapWebServiceReleaseNotFound),
            HttpProblemMapping.For<WebServiceDeploymentTokenNotFound>(
                MapWebServiceDeploymentTokenNotFound),
            HttpProblemMapping.For<WebServiceLifecycleMutationStateError>(
                MapLifecycleMutationStateError),
            HttpProblemMapping.For<RollbackWebServiceStateError>(
                MapRollbackStateError),
            HttpProblemMapping.For<WebServiceDeploymentMutationStateError>(
                MapDeploymentMutationStateError),
            HttpProblemMapping.For<DeleteWebServiceDeploymentStateError>(
                MapDeleteDeploymentStateError),
            HttpProblemMapping.For<DeleteWebServiceStateError>(MapDeleteStateError),
            HttpProblemMapping.For<SourceBuildWebServiceConfigurationExists>(
                MapSourceBuildWebServiceConfigurationExists),
            HttpProblemMapping.For<GetWebServiceDeploymentHostnamesError>(
                MapGetDeploymentHostnamesError),
            HttpProblemMapping.For<WebServiceImageTagAuthorizationError>(
                MapImageTagAuthorizationError),
            HttpProblemMapping.For<WebServiceImageTagStateError>(
                MapImageTagStateError),
            HttpProblemMapping.For<WebServiceUpdateConflict>(error => Problem(
                HttpStatusCode.Conflict,
                "web-services.update.concurrent-change",
                "Web service changed concurrently",
                ("webServiceId", error.WebServiceId)))
        ]);

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

    private static HttpProblem MapWebServiceReleaseNotFound(
        WebServiceReleaseNotFound error) => Problem(
            HttpStatusCode.NotFound,
            "web-services.release-not-found",
            "Succeeded web service release was not found",
            ("webServiceId", error.WebServiceId),
            ("resourceRevision", error.ResourceRevision));

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
            WebServiceHasNoActiveDeployments empty => Problem(
                HttpStatusCode.Conflict,
                "web-services.no-active-deployments",
                "Web service has no active resource deployments",
                ("webServiceId", empty.WebServiceId))
        };

    private static HttpProblem MapDeploymentMutationStateError(
        WebServiceDeploymentMutationStateError error) => error switch
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

    private static HttpProblem MapSourceBuildWebServiceConfigurationExists(
        SourceBuildWebServiceConfigurationExists error) => Problem(
            HttpStatusCode.Conflict,
            "source-builds.web-service-configuration-exists",
            "Delete the source build configuration before deleting this web service",
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

    public static IServiceCollection AddWebServiceHttpMappings(
        this IServiceCollection services) =>
        services.AddHttpProblemMappings(All);

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
