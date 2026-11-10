using Xunit;

namespace Pole.Testing;

[AttributeUsage(AttributeTargets.Method)]
public sealed class KubernetesFactAttribute : FactAttribute
{
    public KubernetesFactAttribute(params string[] requiredCapabilities) =>
        Skip = KubernetesLabSelection.GetOptInSkipReason(requiredCapabilities);
}
