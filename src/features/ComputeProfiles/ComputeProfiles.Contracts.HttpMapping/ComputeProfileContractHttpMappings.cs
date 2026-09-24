using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.ComputeProfiles.Contracts.HttpMapping;

public static class ComputeProfileContractHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<ComputeProfileResolutionError>(MapResolutionError),
            HttpProblemMapping.For<ComputeProfileNotFound>(MapNotFound),
            HttpProblemMapping.For<ComputeProfileResolutionFenceRejected>(
                MapResolutionFenceRejected)
        ]);

    public static HttpProblem MapResolutionError(
        ComputeProfileResolutionError error) => error switch
        {
            DefaultComputeProfileNotConfigured => Problem(
                HttpStatusCode.Conflict,
                "compute-profiles.default-not-configured",
                "Default compute profile is not configured"),
            ComputeProfileNotFound notFound => MapNotFound(notFound),
            ComputeProfileUnavailable unavailable => Problem(
                HttpStatusCode.UnprocessableEntity,
                "compute-profiles.unavailable",
                "Compute profile is unavailable for this workload",
                ("computeProfileId", unavailable.ComputeProfileId),
                ("reason", ReasonCode(unavailable.Reason)))
        };

    public static HttpProblem MapNotFound(ComputeProfileNotFound notFound) => Problem(
        HttpStatusCode.NotFound,
        "compute-profiles.not-found",
        "Compute profile not found",
        ("computeProfileId", notFound.ComputeProfileId));

    public static HttpProblem MapResolutionFenceRejected(
        ComputeProfileResolutionFenceRejected rejected) => Problem(
        HttpStatusCode.Conflict,
        "compute-profiles.resolution-fence.rejected",
        "Compute profile mapping changed or is no longer available",
        ("computeProfileIds", rejected.ComputeProfileIds));

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
