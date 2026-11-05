using Pole.Features.SourceBuilds.Application.Mongo;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Request;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Execution;

public sealed record SourceBuildJobListQuery(
    string TenantId,
    string? ConfigurationId,
    string? WebServiceId,
    int PageNumber,
    int PageSize);

public sealed record SourceBuildJobPage(
    IReadOnlyList<SourceBuildJob> Items,
    long TotalCount,
    int PageNumber,
    int PageSize)
{
    public PaginatedResponse<T> Map<T>(Func<SourceBuildJob, T> map) =>
        new(Items.Select(map).ToArray(), TotalCount, PageNumber, PageSize);
}

public delegate Task<SourceBuildJobPage> LoadSourceBuildJobPage(
    SourceBuildJobListQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceBuildJobPage(IMongoCollection<SourceBuildJob> jobs)
{
    public async Task<SourceBuildJobPage> InvokeAsync(
        SourceBuildJobListQuery query,
        CancellationToken cancellationToken = default)
    {
        var filter = SourceBuildJobFilters.ForList(query);
        var totalCount = await jobs.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var items = await jobs
            .Find(filter)
            .SortByDescending(item => item.CreatedAtUtc)
            .ApplyPagination(query.PageNumber, query.PageSize)
            .ToListAsync(cancellationToken);
        return new SourceBuildJobPage(items, totalCount, query.PageNumber, query.PageSize);
    }
}
