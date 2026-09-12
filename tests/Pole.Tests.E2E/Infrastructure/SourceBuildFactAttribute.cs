using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SourceBuildFactAttribute : FactAttribute
{
    public SourceBuildFactAttribute()
    {
        if (KubernetesLabSelection.GetOptInSkipReason("harbor", "buildkit", "monitoring") is { } labSkip)
        {
            Skip = labSkip;
        }
    }
}
