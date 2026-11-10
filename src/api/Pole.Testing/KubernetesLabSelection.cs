namespace Pole.Testing;

public static class KubernetesLabSelection
{
    public const string StateEnvironmentVariable = "POLE_K8S_LAB_STATE";
    public const string TargetEnvironmentVariable = "POLE_K8S_LAB_TARGET";
    public const string DefaultTarget = "default";

    public static KubernetesLabTarget RequireSelected() => RequireSelection().Target;

    public static SelectedKubernetesLab RequireSelection()
    {
        var state = KubernetesLabState.Load(ResolveStatePath());
        return new SelectedKubernetesLab(state, state.GetRequiredTarget(ResolveTargetKey()));
    }

    public static string ResolveStatePath() =>
        Environment.GetEnvironmentVariable(StateEnvironmentVariable) is { } configured &&
        !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : Path.Combine(RepositoryPaths.FindSharedRoot(), ".tmp", "kubernetes-lab", "state.json");

    public static string ResolveTargetKey() =>
        Environment.GetEnvironmentVariable(TargetEnvironmentVariable) is { } configured &&
        !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : DefaultTarget;

    public static string? GetOptInSkipReason(params string[] requiredCapabilities)
    {
        try
        {
            var target = KubernetesLabState.Load(ResolveStatePath()).GetRequiredTarget(ResolveTargetKey());
            var missing = requiredCapabilities
                .Where(capability => !string.IsNullOrWhiteSpace(capability) && !target.HasCapability(capability))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return missing.Length == 0
                ? null
                : $"Kubernetes lab target '{target.Key}' lacks required capabilities: {string.Join(", ", missing)}.";
        }
        catch
        {
            // Missing, malformed, or unavailable default state must fail in fixture setup instead
            // of being hidden as an opt-in skip.
            return null;
        }
    }
}

public sealed record SelectedKubernetesLab(
    KubernetesLabState State,
    KubernetesLabTarget Target
);
