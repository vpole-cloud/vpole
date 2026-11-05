using MongoDB.Driver;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Features.ResourceConnections.Application.Recovery;

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
