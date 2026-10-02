using MongoDB.Driver;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal sealed record PrivateAccessResolvedEndpoint(
    string Id,
    string ServiceName,
    int ServicePort,
    int TargetPort);

internal sealed class PrivateAccessEndpointCatalog(
    IEnumerable<IResourceConnectionDestinationCapability> capabilities)
{
    private readonly IReadOnlyDictionary<ResourceTypeId, IResourceConnectionDestinationCapability>
        destinations = Build(capabilities);

    public async Task<PrivateAccessResolvedEndpoint?> ResolveAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string endpointId,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!destinations.TryGetValue(resourceType, out var destination))
            return null;
        var result = await destination.LoadAsync(tenantId, resourceId, session, cancellationToken);
        return result switch
        {
            ResourceConnectionEndpointsFound found => Resolve(found.Endpoints, endpointId),
            ResourceConnectionDestinationMissing => null,
            ResourceConnectionDestinationDeleting => null
        };
    }

    public async Task<PrivateAccessResolvedEndpoint?> ResolveReadOnlyAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string endpointId,
        CancellationToken cancellationToken)
    {
        if (!destinations.TryGetValue(resourceType, out var destination))
            return null;
        var found = await destination.LoadAvailableAsync(
            tenantId, [resourceId], cancellationToken);
        return found.TryGetValue(resourceId, out var endpoints)
            ? Resolve(endpoints.Endpoints, endpointId)
            : null;
    }

    private static PrivateAccessResolvedEndpoint? Resolve(
        IReadOnlyCollection<ResourceConnectionEndpoint> endpoints,
        string endpointId)
    {
        if (endpoints.Any(item =>
                string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.ServiceName) ||
                item.ServicePort.Value is < 1 or > 65535 ||
                item.TargetPort.Value is < 1 or > 65535) ||
            endpoints.GroupBy(item => item.Id, StringComparer.Ordinal)
                .Any(group => group.Skip(1).Any()))
            throw new InvalidOperationException("Destination provider published malformed endpoints.");

        var endpoint = endpoints.SingleOrDefault(item =>
            string.Equals(item.Id, endpointId, StringComparison.Ordinal));
        return endpoint is null ? null : new PrivateAccessResolvedEndpoint(
            endpoint.Id, endpoint.ServiceName, endpoint.ServicePort.Value,
            endpoint.TargetPort.Value);
    }

    private static IReadOnlyDictionary<ResourceTypeId, IResourceConnectionDestinationCapability> Build(
        IEnumerable<IResourceConnectionDestinationCapability> capabilities)
    {
        var result = new Dictionary<ResourceTypeId, IResourceConnectionDestinationCapability>();
        foreach (var capability in capabilities)
        {
            if (!result.TryAdd(capability.ResourceType, capability))
                throw new InvalidOperationException("Duplicate private access destination capability.");
        }
        return result;
    }
}
