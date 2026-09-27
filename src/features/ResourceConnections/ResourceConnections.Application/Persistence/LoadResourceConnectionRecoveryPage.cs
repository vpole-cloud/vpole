using MongoDB.Driver;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Features.ResourceConnections.Application.Persistence;

public delegate Task<IReadOnlyList<ResourceConnection>> LoadResourceConnectionRecoveryPage(
    string? afterId,
    int pageSize,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadResourceConnectionRecoveryPage(
    IMongoCollection<ResourceConnection> connections)
{
    public async Task<IReadOnlyList<ResourceConnection>> InvokeAsync(
        string? afterId,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        await connections.Find(ResourceConnectionFilters.AfterId(afterId))
            .SortBy(item => item.Id)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);
}
