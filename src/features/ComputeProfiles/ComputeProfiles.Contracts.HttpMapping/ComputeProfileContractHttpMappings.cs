using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.ComputeProfiles.Contracts.HttpMapping;

public static class ComputeProfileContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<ComputeProfileResolutionError>(MapResolutionError)
        ]);

    private static HttpProblem MapResolutionError(
        ComputeProfileResolutionError error) => error switch
        {
            InvalidComputeProfileResolutionRequest invalid => Problem(
                HttpStatusCode.BadRequest,
                "compute-profiles.resolution.invalid",
                "Compute profile resolution request is invalid",
                ("errors", invalid.Errors)),
            DefaultComputeProfileNotConfigured => Problem(
                HttpStatusCode.Conflict,
                "compute-profiles.default-not-configured",
                "Default compute profile is not configured"),
            ComputeProfileNotFound notFound => Problem(
                HttpStatusCode.NotFound,
                "compute-profiles.not-found",
                "Compute profile not found",
                ("computeProfileId", notFound.ComputeProfileId)),
            ComputeProfileUnavailable unavailable => Problem(
                HttpStatusCode.UnprocessableEntity,
                "compute-profiles.unavailable",
                "Compute profile is unavailable for this workload",
                ("computeProfileId", unavailable.ComputeProfileId),
                ("reason", ReasonCode(unavailable.Reason)))
        };

    private static string ReasonCode(
        ComputeProfileUnavailableReason reason) => reason switch
        {
            ComputeProfileUnavailableReason.Disabled => "disabled",
            ComputeProfileUnavailableReason.ResourceTypeNotAllowed =>
                "resource-type-not-allowed",
            ComputeProfileUnavailableReason.TenantNotAllowed =>
                "tenant-not-allowed",
            ComputeProfileUnavailableReason.DeploymentTargetNotConfigured =>
                "deployment-target-not-configured",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
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
