using Pole.Features.RedisServices.Application.Mongo;
using Pole.Features.RedisServices.Application.Declarations;
using Pole.Features.RedisServices.Application;
using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Workloads;

namespace Pole.Features.RedisServices.Application.Services;

internal sealed class MongoLoadRedisConnectionEndpoints(IMongoCollection<Redis> services)
    : IResourceConnectionDestinationCapability, IResourceConnectionDestinationNameCapability
{
    public string Kind => "redis";

    public ResourceTypeId ResourceType => RedisResourceDeployment.ResourceType;

    public async Task<string?> ResolveResourceIdAsync(
        string tenantId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var matches = await services.Find(RedisFilters.ByName(tenantId, name))
            .Limit(2)
            .ToListAsync(cancellationToken);
        return ResourceConnectionNameResolution.ResolveUniqueResourceId(
            matches,
            resource => resource.Lifecycle is not DeletingRedisLifecycle,
            resource => resource.Id);
    }

    public async Task<LoadResourceConnectionEndpointsOutcome> LoadAsync(
        LoadResourceConnectionDestinationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection endpoint lookup requires a transaction.");
        var resource = await services.Find(session, RedisFilters.ById(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (resource is null)
            return new ResourceConnectionDestinationMissing();
        if (resource.Lifecycle is DeletingRedisLifecycle)
            return new ResourceConnectionDestinationDeleting();
        return Endpoints(resource);
    }

    public async Task<IReadOnlyDictionary<string, ResourceConnectionEndpointsFound>> LoadAvailableAsync(
        string tenantId,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        if (resourceIds.Count == 0)
            return new Dictionary<string, ResourceConnectionEndpointsFound>();
        var resources = await services.Find(RedisFilters.ByIds(tenantId, resourceIds))
            .ToListAsync(cancellationToken);
        return resources
            .Where(resource => resource.Lifecycle is not DeletingRedisLifecycle)
            .ToDictionary(resource => resource.Id, Endpoints, StringComparer.Ordinal);
    }

    private static ResourceConnectionEndpointsFound Endpoints(Redis resource)
    {
        var endpoint = RedisConnectionEndpoints.Redis;
        return new ResourceConnectionEndpointsFound(
            ImmutableList.Create(new ResourceConnectionEndpoint(
                endpoint.Id,
                RedisInKubernetes.BuildServiceName(resource.Name),
                NetworkPort.Create(endpoint.TcpPort),
                NetworkPort.Create(endpoint.TcpPort))
            {
                DisplayName = "Redis",
                Protocol = "redis",
                Configuration = [new("database", "Database index", "0")]
            }));
    }
}
