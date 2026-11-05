using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Connections;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application;
using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Queries;

public sealed record WebServiceListQuery(
    string TenantId,
    string? Search,
    string? ResourceGroupId,
    int PageNumber,
    int PageSize)
{
    public int Skip => checked((PageNumber - 1) * PageSize);
}

public sealed record WebServicePage(
    IReadOnlyList<WebService> Items,
    long TotalCount);

public delegate Task<WebServicePage> LoadWebServicePage(
    WebServiceListQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadWebServicePage(IMongoCollection<WebService> webServices)
{
    public async Task<WebServicePage> InvokeAsync(
        WebServiceListQuery query,
        CancellationToken cancellationToken = default)
    {
        var filter = WebServiceFilters.ForList(query);

        var count = await webServices.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken);
        var items = await webServices.Find(filter)
            .SortBy(service => service.Name)
            .ThenBy(service => service.Id)
            .Skip(query.Skip)
            .Limit(query.PageSize)
            .ToListAsync(cancellationToken);
        return new WebServicePage(items, count);
    }
}
