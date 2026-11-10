using MongoDB.Driver;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Topology;

internal sealed record ResolvedConnectionEndpoint(
    string Id,
    string ServiceName,
    NetworkPort ServicePort,
    NetworkPort TargetPort
)
{
    internal IReadOnlyList<ResourceConnectionConfigurationField> Configuration { get; init; } = [];
}

internal sealed record ConnectionEndpointMissing;

internal sealed record ConnectionDestinationDeleting;

internal sealed record ConnectionEndpointUnsupportedByProvider;

internal union ResolveConnectionEndpointOutcome(
    ResolvedConnectionEndpoint,
    ConnectionEndpointMissing,
    ConnectionDestinationDeleting,
    ConnectionEndpointUnsupportedByProvider);

public sealed class ResourceConnectionEndpointCatalog
{
    private readonly IReadOnlyDictionary<ResourceTypeId, IResourceConnectionDestinationCapability> destinations;

    private readonly HashSet<ResourceTypeId> sources;

    public ResourceConnectionEndpointCatalog(
        IEnumerable<ResourceConnectionSourceCapability> sourceCapabilities,
        IEnumerable<IResourceConnectionDestinationCapability> destinationCapabilities)
    {
        sources = new HashSet<ResourceTypeId>();
        foreach (var source in sourceCapabilities)
        {
            if (string.IsNullOrWhiteSpace(source.ResourceType.Value))
                throw new InvalidOperationException("A resource connection source type is invalid.");
            if (!sources.Add(source.ResourceType))
                throw new InvalidOperationException(
                    $"Resource connection source type '{source.ResourceType.Value}' is registered more than once.");
        }

        var registered = new Dictionary<ResourceTypeId, IResourceConnectionDestinationCapability>();
        foreach (var destination in destinationCapabilities)
        {
            if (string.IsNullOrWhiteSpace(destination.ResourceType.Value))
                throw new InvalidOperationException("A resource connection destination type is invalid.");
            if (!registered.TryAdd(destination.ResourceType, destination))
                throw new InvalidOperationException(
                    $"Resource connection destination type '{destination.ResourceType.Value}' is registered more than once.");
        }

        destinations = registered;
    }

    public bool SupportsSource(ResourceTypeId type) => sources.Contains(type);

    public bool SupportsDestination(ResourceTypeId type) => destinations.ContainsKey(type);

    public async Task<ResourceConnectionEndpointsFound?> LoadAvailableAsync(
        string tenantId,
        ResourceTypeId type,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        if (!destinations.TryGetValue(type, out var provider))
            return null;
        var found = await provider.LoadAvailableAsync(tenantId, [resourceId], cancellationToken);
        if (!found.TryGetValue(resourceId, out var endpoints))
            return null;
        Validate(endpoints.Endpoints);
        return endpoints;
    }

    internal async Task<ResolveConnectionEndpointOutcome> ResolveAsync(
        string tenantId,
        ResourceTypeId destinationType,
        string resourceId,
        string endpointId,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!destinations.TryGetValue(destinationType, out var destination))
            return new ConnectionEndpointUnsupportedByProvider();

        var result = await destination.LoadAsync(
            new LoadResourceConnectionDestinationRequest(TenantId: tenantId, ResourceId: resourceId),
            session: session,
            cancellationToken: cancellationToken);
        return result switch
        {
            ResourceConnectionEndpointsFound found => Resolve(found.Endpoints, endpointId),
            ResourceConnectionDestinationMissing => new ConnectionEndpointMissing(),
            ResourceConnectionDestinationDeleting => new ConnectionDestinationDeleting(),
        };
    }

    private static ResolveConnectionEndpointOutcome Resolve(
        IEnumerable<ResourceConnectionEndpoint> endpoints,
        string endpointId)
    {
        var published = endpoints.ToArray();
        Validate(published);
        var endpoint = published.SingleOrDefault(item => string.Equals(item.Id, endpointId, StringComparison.Ordinal));
        return endpoint is null
            ? new ConnectionEndpointUnsupportedByProvider()
            : new ResolvedConnectionEndpoint(
                endpoint.Id,
                endpoint.ServiceName,
                endpoint.ServicePort,
                endpoint.TargetPort) { Configuration = endpoint.Configuration };
    }

    private static void Validate(IEnumerable<ResourceConnectionEndpoint> endpoints)
    {
        var published = endpoints.ToArray();
        if (published.GroupBy(item => item.Id, StringComparer.Ordinal).Any(group => group.Skip(1).Any()))
            throw new InvalidOperationException("A destination provider published duplicate connection endpoint IDs.");
        if (published.Any(item => string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.ServiceName) ||
                !ValidText(item.DisplayName, 200) || !ValidText(item.Protocol, 64) || item.Transport != "TCP" ||
                item.Configuration is null || item.Configuration.Count > 32 ||
                item.Configuration.Any(field =>
                    !ValidText(field.Key, 128) || !ValidText(field.DisplayName, 200) || !ValidText(field.Value, 500)) ||
                item.Configuration
                    .GroupBy(field => field.Key, StringComparer.Ordinal)
                    .Any(group => group.Skip(1).Any()) || !NetworkPort.TryCreate(item.ServicePort.Value, out _) ||
                !NetworkPort.TryCreate(item.TargetPort.Value, out _)))
            throw new InvalidOperationException("A destination provider published an invalid connection endpoint.");
    }

    private static bool ValidText(string value, int length) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= length && !value.Any(char.IsControl);
}
