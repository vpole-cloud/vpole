using MongoDB.Driver;
using Pole.Features.Postgres.Contracts;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.WebServices.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Persistence;

internal sealed record ResolvedConnectionEndpoint(
    string Id,
    string ServiceName,
    int ServicePort,
    int TargetPort);

internal sealed record ConnectionEndpointMissing;

internal sealed record ConnectionDestinationDeleting;

internal sealed record ConnectionEndpointUnsupportedByProvider;

internal union ResolveConnectionEndpointOutcome(
    ResolvedConnectionEndpoint,
    ConnectionEndpointMissing,
    ConnectionDestinationDeleting,
    ConnectionEndpointUnsupportedByProvider);

internal sealed class ResourceConnectionEndpointCatalog(
    IEnumerable<LoadRedisConnectionEndpoints> redisReaders,
    IEnumerable<LoadWebServiceConnectionEndpoints> webReaders,
    IEnumerable<LoadPostgresConnectionEndpoints> postgresReaders)
{
    private readonly LoadRedisConnectionEndpoints? loadRedis = redisReaders.SingleOrDefault();
    private readonly LoadWebServiceConnectionEndpoints? loadWeb = webReaders.SingleOrDefault();
    private readonly LoadPostgresConnectionEndpoints? loadPostgres = postgresReaders.SingleOrDefault();
    private static readonly ResourceTypeId WebServiceType =
        ResourceTypeId.Create("pole/web-service");
    private static readonly ResourceTypeId PostgresType =
        ResourceTypeId.Create("pole/postgresql");

    public async Task<ResolveConnectionEndpointOutcome> ResolveAsync(
        string tenantId,
        ResourceTypeId destinationType,
        string resourceId,
        string endpointId,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (destinationType == RedisConnectionEndpoints.ResourceType)
        {
            if (loadRedis is null)
                return new ConnectionEndpointUnsupportedByProvider();
            var result = await loadRedis(tenantId, resourceId, session, cancellationToken);
            return result switch
            {
                RedisConnectionEndpointsFound found => Resolve(
                    found.ServiceName,
                    found.Endpoints.Select(item => new ResolvedConnectionEndpoint(
                        item.Id, found.ServiceName, item.ServicePort, item.TargetPort)),
                    endpointId),
                RedisConnectionResourceMissing => new ConnectionEndpointMissing(),
                RedisConnectionResourceDeleting => new ConnectionDestinationDeleting()
            };
        }
        if (destinationType == WebServiceType)
        {
            if (loadWeb is null)
                return new ConnectionEndpointUnsupportedByProvider();
            var result = await loadWeb(tenantId, resourceId, session, cancellationToken);
            return result switch
            {
                WebServiceConnectionEndpointsFound found => Resolve(
                    found.ServiceName,
                    found.Endpoints.Select(item => new ResolvedConnectionEndpoint(
                        item.Id, found.ServiceName, item.ServicePort, item.TargetPort)),
                    endpointId),
                WebServiceConnectionResourceMissing => new ConnectionEndpointMissing(),
                WebServiceConnectionResourceDeleting => new ConnectionDestinationDeleting()
            };
        }
        if (destinationType == PostgresType)
        {
            if (loadPostgres is null)
                return new ConnectionEndpointUnsupportedByProvider();
            var result = await loadPostgres(tenantId, resourceId, session, cancellationToken);
            return result switch
            {
                PostgresConnectionEndpointsFound found => Resolve(
                    found.ServiceName,
                    found.Endpoints.Select(item => new ResolvedConnectionEndpoint(
                        item.Id, found.ServiceName, item.ServicePort, item.TargetPort)),
                    endpointId),
                PostgresConnectionResourceMissing => new ConnectionEndpointMissing(),
                PostgresConnectionResourceDeleting => new ConnectionDestinationDeleting()
            };
        }
        return new ConnectionEndpointUnsupportedByProvider();
    }

    public static bool SupportsDestination(ResourceTypeId type) =>
        type == RedisConnectionEndpoints.ResourceType ||
        type == WebServiceType ||
        type == PostgresType;

    private static ResolveConnectionEndpointOutcome Resolve(
        string serviceName,
        IEnumerable<ResolvedConnectionEndpoint> endpoints,
        string endpointId)
    {
        var endpoint = endpoints.SingleOrDefault(item =>
            string.Equals(item.Id, endpointId, StringComparison.Ordinal));
        return endpoint is null || string.IsNullOrWhiteSpace(serviceName)
            ? new ConnectionEndpointUnsupportedByProvider()
            : endpoint;
    }
}
