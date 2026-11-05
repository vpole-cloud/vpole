using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Mongo;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Catalog;

public sealed record ComputeProfileListQuery(
    int PageNumber,
    int PageSize)
{
    public int Skip => checked((PageNumber - 1) * PageSize);
}

public sealed record ComputeProfilePage(
    IReadOnlyList<ComputeProfile> Items,
    long TotalCount);

public delegate Task<ComputeProfilePage> LoadComputeProfilePage(
    ComputeProfileListQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadComputeProfilePage(
    IMongoCollection<ComputeProfile> computeProfiles)
{
    public async Task<ComputeProfilePage> InvokeAsync(
        ComputeProfileListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = ComputeProfileFilters.All();
        var totalCount = await computeProfiles.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken);
        var profiles = await computeProfiles
            .Find(filter)
            .SortByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.DisplayName)
            .ThenBy(profile => profile.Id)
            .Skip(query.Skip)
            .Limit(query.PageSize)
            .ToListAsync(cancellationToken);
        return new ComputeProfilePage(profiles, totalCount);
    }
}
