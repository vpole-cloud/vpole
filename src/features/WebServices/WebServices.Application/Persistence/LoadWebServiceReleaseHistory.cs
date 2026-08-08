using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

public sealed record WebServiceReleaseHistoryQuery(
    string TenantId,
    string WebServiceId,
    int PageNumber,
    int PageSize)
{
    public int Skip => checked((PageNumber - 1) * PageSize);
}

public sealed record WebServiceReleaseHistory(
    WebService WebService,
    IReadOnlyList<WebServiceRelease> Releases,
    long TotalCount);

public delegate Task<WebServiceReleaseHistory?> LoadWebServiceReleaseHistory(
    WebServiceReleaseHistoryQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadWebServiceReleaseHistory(
    IMongoCollection<WebService> webServices,
    IMongoCollection<WebServiceRelease> releases)
{
    public async Task<WebServiceReleaseHistory?> InvokeAsync(
        WebServiceReleaseHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var webService = await webServices
            .Find(WebServiceFilters.ById(query.TenantId, query.WebServiceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (webService is null)
            return null;

        var filter = WebServiceReleaseFilters.ByWebService(
            query.TenantId,
            query.WebServiceId);
        var count = await releases.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken);
        var items = await releases.Find(filter)
            .SortByDescending(item => item.Revision)
            .Skip(query.Skip)
            .Limit(query.PageSize)
            .ToListAsync(cancellationToken);
        return new WebServiceReleaseHistory(webService, items, count);
    }
}
