using Pole.Features.ComputeProfiles.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal static class ComputeProfileResolutionLegacyFailures
{
    public static Failure ToFailure(ComputeProfileResolutionError error) => error switch
    {
        DefaultComputeProfileNotConfigured =>
            Failures.Conflict("The default compute profile is not configured."),
        ComputeProfileNotFound notFound => Failures.NotFound(
            "Compute profile not found.",
            notFound.ComputeProfileId),
        ComputeProfileUnavailable unavailable => Failures.SemanticValidation(
            new Dictionary<string, string[]>
            {
                ["ComputeProfileId"] = [UnavailableMessage(unavailable.Reason)]
            })
    };

    private static string UnavailableMessage(
        ComputeProfileUnavailableReason reason) => reason switch
    {
        ComputeProfileUnavailableReason.Disabled =>
            "Compute profile is disabled.",
        ComputeProfileUnavailableReason.ResourceTypeNotAllowed =>
            "Compute profile is not available for this resource type.",
        ComputeProfileUnavailableReason.TenantNotAllowed =>
            "Compute profile is not available to the active tenant.",
        ComputeProfileUnavailableReason.DeploymentTargetNotConfigured =>
            "Compute profile is not configured for the selected deployment target.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };
}
