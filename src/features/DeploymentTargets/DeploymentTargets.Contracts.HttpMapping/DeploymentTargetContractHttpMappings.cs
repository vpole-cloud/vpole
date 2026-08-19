using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.DeploymentTargets.Contracts.HttpMapping;

public static class DeploymentTargetContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<DeploymentTargetDomainReservationAdmissionError>(
                MapDomainReservationAdmissionError)
        ]);

    private static HttpProblem MapDomainReservationAdmissionError(
        DeploymentTargetDomainReservationAdmissionError error) => error switch
    {
        InvalidDeploymentTargetId invalid => ForTarget(
            HttpStatusCode.BadRequest,
            "Deployment target ID is invalid",
            "deployment-targets.id.invalid",
            invalid.DeploymentTargetId),
        DeploymentTargetNotFound notFound => ForTarget(
            HttpStatusCode.NotFound,
            "Deployment target not found",
            "deployment-targets.not-found",
            notFound.DeploymentTargetId),
        DeploymentTargetDisabled disabled => ForTarget(
            HttpStatusCode.Conflict,
            "Deployment target is disabled",
            "deployment-targets.disabled",
            disabled.DeploymentTargetId),
        DeploymentTargetDeletionRequested deleting => ForTarget(
            HttpStatusCode.Conflict,
            "Deployment target is pending deletion",
            "deployment-targets.deletion-requested",
            deleting.DeploymentTargetId),
        DeploymentTargetNewWorkloadsDisabled unavailable => ForTarget(
            HttpStatusCode.Conflict,
            "Deployment target does not accept new reservations",
            "deployment-targets.new-workloads-disabled",
            unavailable.DeploymentTargetId),
        DeploymentTargetPublicIngressUnavailable unavailable => ForTarget(
            HttpStatusCode.UnprocessableEntity,
            "Deployment target has no public ingress",
            "deployment-targets.public-ingress-unavailable",
            unavailable.DeploymentTargetId)
    };

    private static HttpProblem ForTarget(
        HttpStatusCode statusCode,
        string title,
        string code,
        string deploymentTargetId) =>
        new(
            (int)statusCode,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions: new Dictionary<string, object>
            {
                ["deploymentTargetId"] = deploymentTargetId
            });
}
