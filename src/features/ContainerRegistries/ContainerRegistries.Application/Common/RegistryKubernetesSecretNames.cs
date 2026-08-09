namespace Pole.Features.ContainerRegistries.Application.Common;

internal static class RegistryKubernetesSecretNames
{
    public static string ForProject(string registryProjectId) =>
        $"pole-registry-{registryProjectId.ToLowerInvariant()}";
}
