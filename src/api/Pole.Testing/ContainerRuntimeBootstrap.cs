namespace Pole.Testing;

public static class ContainerRuntimeBootstrap
{
    public static void ConfigureFromLabStateIfAvailable()
    {
        var statePath = KubernetesLabSelection.ResolveStatePath();
        if (!File.Exists(statePath))
            return;

        Configure(KubernetesLabState.Load(statePath).Runtime);
    }

    public static void Configure(KubernetesLabRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        SetIfMissing("DOCKER_HOST", runtime.DockerHost);
        SetIfMissing("DOCKER_SOCK", runtime.DockerSocketPath);
        SetIfMissing("TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE", runtime.DockerSocketPath);
        SetIfMissing("ASPIRE_CONTAINER_RUNTIME", runtime.Name);
        if (runtime.TestcontainersRyukDisabled)
            SetIfMissing("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private static void SetIfMissing(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }
}
