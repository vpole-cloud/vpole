using MongoDB.Driver;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Features.ResourceConnections.Application.Persistence;

public delegate Task<ResourceConnection?> LoadResourceConnection(
    string tenantId,
    string connectionId,
    CancellationToken cancellationToken = default);

public sealed record ResourceConnectionPage(
    IReadOnlyList<ResourceConnection> Items,
    long TotalCount);

public delegate Task<ResourceConnectionPage> LoadResourceConnectionsPage(
    string tenantId,
    int pageNumber,
    int pageSize,
    CancellationToken cancellationToken = default);

internal sealed class MongoResourceConnectionReads(
    IMongoCollection<ResourceConnection> connections)
{
    public async Task<ResourceConnection?> LoadOneAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default) =>
        await connections.Find(ResourceConnectionFilters.ByTenantAndId(tenantId, connectionId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<ResourceConnectionPage> LoadPageAsync(
        string tenantId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var filter = ResourceConnectionFilters.ByTenant(tenantId);
        var count = await connections.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken);
        var items = await connections.Find(filter)
            .SortBy(item => item.Name)
            .Skip(checked((pageNumber - 1) * pageSize))
            .Limit(pageSize)
            .ToListAsync(cancellationToken);
        return new ResourceConnectionPage(items, count);
    }
}
