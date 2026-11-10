namespace Pole.Features.DeploymentTargets.Application.Common;

internal static class DeploymentTargetClock
{
    public static DateTime UtcNowAfter(TimeProvider timeProvider, DateTime? floor)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return floor is { } value && value > now ? value : now;
    }
}
