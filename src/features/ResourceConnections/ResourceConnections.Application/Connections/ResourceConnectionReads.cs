using MongoDB.Driver;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Connections;

public delegate Task<ResourceConnection?> LoadResourceConnection(
    string tenantId,
    string connectionId,
    CancellationToken cancellationToken = default);

public sealed record ResourceConnectionPage(
    IReadOnlyList<ResourceConnection> Items,
    long TotalCount
);

public delegate Task<ResourceConnectionPage> LoadResourceConnectionsPage(
    string tenantId,
    int pageNumber,
    int pageSize,
    CancellationToken cancellationToken = default,
    ResourceTypeId? sourceType = null,
    string? sourceId = null);

internal sealed class MongoResourceConnectionReads(
    IMongoCollection<ResourceConnection> connections
)
{
    public async Task<ResourceConnection?> LoadOneAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default) => await connections
        .Find(ResourceConnectionFilters.ByTenantAndId(tenantId, connectionId))
        .FirstOrDefaultAsync(cancellationToken);

    public async Task<ResourceConnectionPage> LoadPageAsync(
        string tenantId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default,
        ResourceTypeId? sourceType = null,
        string? sourceId = null)
    {
        var filter = sourceType is null
            ? ResourceConnectionFilters.ByTenant(tenantId)
            : ResourceConnectionFilters.BySource(tenantId, sourceType.Value, sourceId!);
        var count = await connections.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var items = await connections.Find(filter)
            .SortBy(item => item.Name)
            .ThenBy(item => item.Id)
            .Skip(checked((pageNumber - 1) * pageSize))
            .Limit(pageSize)
            .ToListAsync(cancellationToken);
        return new ResourceConnectionPage(items, count);
    }
}
