using System.Net;
using Microsoft.AspNetCore.Http;
using Pole.ASP.Helpers;
using Pole.Features.ResourceDeployments.Application.Endpoints;

namespace Pole.Features.ResourceDeployments.Application.Common;

public static class ResourceDeploymentHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<RetryResourceDeploymentError>(
                MapRetryProblem)
        ]);

    public static IResult MapRetryError(RetryResourceDeploymentError error) =>
        MapRetryProblem(error).ToHttpResult();

    private static HttpProblem MapRetryProblem(
        RetryResourceDeploymentError error) => error switch
        {
            ResourceDeploymentNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "Resource deployment not found",
                "resource-deployments.not-found",
                notFound.DeploymentId),
            ResourceDeploymentNotRetryable notRetryable => Problem(
                HttpStatusCode.Conflict,
                "Resource deployment cannot be retried",
                "resource-deployments.retry.not-retryable",
                notRetryable.DeploymentId),
            ResourceDeploymentRetryConflict conflict => Problem(
                HttpStatusCode.Conflict,
                "Resource deployment changed during retry",
                "resource-deployments.retry.concurrent-change",
                conflict.DeploymentId)
        };

    private static HttpProblem Problem(
        HttpStatusCode statusCode,
        string title,
        string code,
        string deploymentId) =>
        new(
            (int)statusCode,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions: new Dictionary<string, object>
            {
                ["deploymentId"] = deploymentId
            });
}
