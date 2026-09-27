using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence.Reads;

internal sealed class MongoLoadRedisConnectionEndpoints(IMongoCollection<Redis> services)
{
    public async Task<LoadRedisConnectionEndpointsOutcome> InvokeAsync(
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
            return new RedisConnectionResourceMissing();
        if (resource.Lifecycle is DeletingRedisLifecycle)
            return new RedisConnectionResourceDeleting();
        var endpoint = RedisConnectionEndpoints.Redis;
        return new RedisConnectionEndpointsFound(
            resource.Id,
            RedisInKubernetes.BuildServiceName(resource.Name),
            ImmutableList.Create(new RedisConnectionEndpointDto(
                endpoint.Id, endpoint.TcpPort, endpoint.TcpPort)));
    }
}
