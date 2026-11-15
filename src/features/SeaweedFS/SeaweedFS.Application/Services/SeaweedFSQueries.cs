using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SeaweedFS.Application.Mongo;
using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application.Services;

public sealed class SeaweedFSQueries(
    IMongoCollection<SeaweedFSService> services
)
{
    public async Task<(IReadOnlyList<SeaweedFSService> Items, long Count)> PageAsync(
        string tenant,
        string? search,
        string? group,
        int page,
        int size,
        CancellationToken ct)
    {
        var filter = SeaweedFSFilters.PublicList(tenant, search, group);
        var count = await services.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await services.Find(filter)
            .SortBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Skip(checked((page - 1) * size))
            .Limit(size)
            .ToListAsync(ct);
        return (items, count);
    }
}
