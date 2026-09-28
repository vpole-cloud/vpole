namespace Pole.Features.ResourceConnections.Application.Kubernetes;

internal static class ResourceConnectionServiceDns
{
    public static string Build(string serviceName, string namespaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        return $"{serviceName}.{namespaceName}.svc.cluster.local";
    }
}
