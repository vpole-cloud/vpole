namespace Pole.Features.ResourceGroups.Application.Common;

internal static class RuntimeCellVerificationSchedule
{
    public static DateTime DueBefore(DateTime now) => now.AddMinutes(-5);
}
