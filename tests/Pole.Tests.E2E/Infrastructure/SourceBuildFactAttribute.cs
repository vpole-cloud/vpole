using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SourceBuildFactAttribute : FactAttribute
{
    public SourceBuildFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("POLE_E2E_SOURCE_BUILDS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set POLE_E2E_SOURCE_BUILDS=1 and configure the prepared Harbor, BuildKit, and Kubernetes runner to run this lane.";
        }
    }
}
