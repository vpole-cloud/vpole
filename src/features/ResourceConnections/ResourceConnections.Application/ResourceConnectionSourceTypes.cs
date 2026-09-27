using Pole.Features.RedisServices.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application;

// These providers publish stable resource-deployment identity labels on their
// workload pods. A source provider joins only after that invariant is verified.
internal static class ResourceConnectionSourceTypes
{
    private static readonly HashSet<ResourceTypeId> Supported =
    [
        ResourceTypeId.Create("pole/web-service"),
        RedisConnectionEndpoints.ResourceType,
        ResourceTypeId.Create("pole/postgresql"),
        ResourceTypeId.Create("pole/function-app")
    ];

    public static bool Contains(ResourceTypeId resourceType) => Supported.Contains(resourceType);
}
