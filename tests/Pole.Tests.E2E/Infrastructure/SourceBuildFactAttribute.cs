using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SourceBuildFactAttribute : FactAttribute
{
    public SourceBuildFactAttribute()
    {
        if (E2ETestSettings.Load().SourceBuilds is null)
        {
            Skip = "Configure SourceBuilds in tests/Pole.Tests.E2E/testsettings.local.json to run this lane.";
        }
    }
}
