using Pole.Features.ResourceConnections.Contracts;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal sealed record PrivateAccessNamedResource(
    ResourceTypeId ResourceType,
    string ResourceId);

public sealed class PrivateAccessResourceNameCatalog(
    IEnumerable<IResourceConnectionDestinationNameCapability> capabilities)
{
    private readonly IReadOnlyDictionary<string, IResourceConnectionDestinationNameCapability> destinations =
        Build(capabilities);

    internal async Task<PrivateAccessNamedResource?> ResolveAsync(
        string tenantId,
        string kind,
        string name,
        CancellationToken cancellationToken)
    {
        if (!destinations.TryGetValue(kind, out var destination))
            return null;

        var id = await destination.ResolveResourceIdAsync(tenantId, name, cancellationToken);
        return id is null ? null : new PrivateAccessNamedResource(destination.ResourceType, id);
    }

    private static IReadOnlyDictionary<string, IResourceConnectionDestinationNameCapability> Build(
        IEnumerable<IResourceConnectionDestinationNameCapability> capabilities)
    {
        var result = new Dictionary<string, IResourceConnectionDestinationNameCapability>(
            StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability.Kind) ||
                !result.TryAdd(capability.Kind, capability))
                throw new InvalidOperationException("Duplicate or invalid Private Access resource kind.");
        }

        return result;
    }
}
