using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Workloads;

namespace Pole.Features.RedisServices.Application.Persistence.Reads;

internal sealed class MongoLoadRedisConnectionEndpoints(IMongoCollection<Redis> services)
    : IResourceConnectionDestinationCapability
{
    public ResourceTypeId ResourceType => RedisResourceDeployment.ResourceType;

    public async Task<LoadResourceConnectionEndpointsOutcome> LoadAsync(
        string tenantId,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection endpoint lookup requires a transaction.");
        var resource = await services.Find(session, RedisFilters.ById(tenantId, resourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (resource is null)
            return new ResourceConnectionDestinationMissing();
        if (resource.Lifecycle is DeletingRedisLifecycle)
            return new ResourceConnectionDestinationDeleting();
        var endpoint = RedisConnectionEndpoints.Redis;
        return new ResourceConnectionEndpointsFound(
            ImmutableList.Create(new ResourceConnectionEndpoint(
                endpoint.Id,
                RedisInKubernetes.BuildServiceName(resource.Name),
                NetworkPort.Create(endpoint.TcpPort),
                NetworkPort.Create(endpoint.TcpPort))));
    }
}
