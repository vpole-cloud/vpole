namespace Pole.Features.ResourceDeployments.Application.Common;

internal static class ResourceDeploymentVerificationSchedule
{
    public static DateTime DueBefore(DateTime now) => now.AddMinutes(-5);
}
