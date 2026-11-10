using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.DeploymentTargets.Contracts.HttpMapping;

public static class DeploymentTargetContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } = Array.AsReadOnly<HttpProblemMapping>(
    [
        HttpProblemMapping.For<DeploymentTargetLookupError>(MapLookupError),
        HttpProblemMapping.For<DefaultDeploymentTargetLookupError>(MapDefaultLookupError),
        HttpProblemMapping.For<DefaultDeploymentTargetWorkloadAdmissionError>(MapDefaultWorkloadAdmissionError),
        HttpProblemMapping.For<DeploymentTargetWorkloadAdmissionError>(MapWorkloadAdmissionError),
        HttpProblemMapping.For<DeploymentTargetNewWorkloadAdmissionError>(MapNewWorkloadAdmissionError),
        HttpProblemMapping.For<DeploymentTargetReferenceChangeRejected>(MapReferenceChangeRejected),
        HttpProblemMapping.For<DeploymentTargetPublicIngressAdmissionError>(MapPublicIngressAdmissionError),
        HttpProblemMapping.For<DeploymentTargetDomainReservationAdmissionError>(MapDomainReservationAdmissionError),
    ]);

    public static HttpProblem MapNotFound(DeploymentTargetNotFound error) =>
        MapLookupError(new DeploymentTargetLookupError(error));

    private static HttpProblem MapLookupError(DeploymentTargetLookupError error) => error switch
    {
        DeploymentTargetNotFound notFound => ForTarget(
            HttpStatusCode.NotFound,
            "Deployment target not found",
            "deployment-targets.not-found",
            notFound.DeploymentTargetId),
    };

    private static HttpProblem MapDefaultLookupError(DefaultDeploymentTargetLookupError error) => error switch
    {
        DefaultDeploymentTargetNotConfigured => Problem(
            HttpStatusCode.Conflict,
            "deployment-targets.default-not-configured",
            "The default deployment target is not configured"),
    };

    private static HttpProblem MapDefaultWorkloadAdmissionError(DefaultDeploymentTargetWorkloadAdmissionError error) =>
        error switch
        {
            DefaultDeploymentTargetNotConfigured => Problem(
                HttpStatusCode.Conflict,
                "deployment-targets.default-not-configured",
                "The default deployment target is not configured"),
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
                "Deployment target does not accept new workloads",
                "deployment-targets.new-workloads-disabled",
                unavailable.DeploymentTargetId),
        };

    public static HttpProblem MapNewWorkloadAdmissionError(DeploymentTargetNewWorkloadAdmissionError error) =>
        error switch
        {
            DeploymentTargetWorkloadAdmissionError workload => MapWorkloadAdmissionError(workload),
            NoDeploymentTargetAcceptsWorkloads => Problem(
                HttpStatusCode.Conflict,
                "deployment-targets.no-workload-target",
                "No deployment target accepts new workloads"),
            DeploymentTargetRequiredCapabilitiesUnavailable unavailable => Problem(
                HttpStatusCode.UnprocessableEntity,
                "deployment-targets.required-capabilities-unavailable",
                "Deployment target does not satisfy the required workload capabilities",
                ("deploymentTargetId", unavailable.DeploymentTargetId),
                ("requiredCapabilities", unavailable.RequiredCapabilities.ToString())),
            DeploymentTargetWorkloadSelectionRequired selection => Problem(
                HttpStatusCode.UnprocessableEntity,
                "deployment-targets.selection-required",
                "A deployment target must be selected",
                ("eligibleDeploymentTargetIds", selection.EligibleDeploymentTargetIds)),
        };

    private static HttpProblem MapWorkloadAdmissionError(DeploymentTargetWorkloadAdmissionError error) => error switch
    {
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
            "Deployment target does not accept new workloads",
            "deployment-targets.new-workloads-disabled",
            unavailable.DeploymentTargetId),
    };

    public static HttpProblem MapReferenceChangeRejected(DeploymentTargetReferenceChangeRejected rejected) => new(
        (int)HttpStatusCode.Conflict,
        PoleProblemTypeId.FromCode("deployment-targets.reference-change-rejected"),
        "Deployment target rejected the reference change",
        "deployment-targets.reference-change-rejected",
        extensions: new Dictionary<string, object>
        {
            ["deploymentTargetIds"] = rejected.DeploymentTargetIds,
            ["ownerType"] = rejected.Owner.Type,
            ["ownerId"] = rejected.Owner.Id,
        });

    public static HttpProblem MapPublicIngressAdmissionError(DeploymentTargetPublicIngressAdmissionError error) =>
        error switch
        {
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
                "Deployment target does not accept new workloads",
                "deployment-targets.new-workloads-disabled",
                unavailable.DeploymentTargetId),
            NoDeploymentTargetAcceptsPublicWorkloads => Problem(
                HttpStatusCode.Conflict,
                "deployment-targets.public-workloads-unavailable",
                "No deployment target accepts public workloads"),
            DeploymentTargetSelectionRequired required => new HttpProblem(
                (int)HttpStatusCode.UnprocessableEntity,
                PoleProblemTypeId.FromCode("deployment-targets.selection-required"),
                "A deployment target must be selected",
                "deployment-targets.selection-required",
                extensions: new Dictionary<string, object>
                {
                    ["eligibleDeploymentTargetIds"] = required.EligibleDeploymentTargetIds,
                }),
            DeploymentTargetPublicIngressUnavailable unavailable => ForTarget(
                HttpStatusCode.UnprocessableEntity,
                "Deployment target has no public ingress",
                "deployment-targets.public-ingress-unavailable",
                unavailable.DeploymentTargetId),
        };

    public static HttpProblem MapDomainReservationAdmissionError(
        DeploymentTargetDomainReservationAdmissionError error) => error switch
    {
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
            unavailable.DeploymentTargetId),
    };

    private static HttpProblem ForTarget(
        HttpStatusCode statusCode,
        string title,
        string code,
        string deploymentTargetId) => new(
        (int)statusCode,
        PoleProblemTypeId.FromCode(code),
        title,
        code,
        extensions: new Dictionary<string, object>
        {
            ["deploymentTargetId"] = deploymentTargetId,
        });

    private static HttpProblem Problem(
        HttpStatusCode statusCode,
        string code,
        string title,
        params (string Name, object Value)[] extensions) => new(
        (int)statusCode,
        PoleProblemTypeId.FromCode(code),
        title,
        code,
        extensions: extensions.ToDictionary(extension => extension.Name, extension => extension.Value));
}
