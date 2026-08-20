using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Persistence;

namespace Pole.Features.WebServices.Application.Common;

public static class WebServiceHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<AddWebServiceDeploymentStateError>(MapStateError),
            HttpProblemMapping.For<WebServiceUpdateConflict>(error => Problem(
                HttpStatusCode.Conflict,
                "web-services.update.concurrent-change",
                "Web service changed concurrently",
                ("webServiceId", error.WebServiceId)))
        ]);

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
