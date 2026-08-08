using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

public sealed record WebServiceListQuery(
    string TenantId,
    string? Search,
    string? ResourceGroupId,
    int PageNumber,
    int PageSize)
{
    public int Skip => checked((PageNumber - 1) * PageSize);
}

public sealed record WebServicePage(IReadOnlyList<WebService> Items, long TotalCount);

public delegate Task<WebServicePage> LoadWebServicePage(
    WebServiceListQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadWebServicePage(IMongoCollection<WebService> webServices)
{
    public async Task<WebServicePage> InvokeAsync(
        WebServiceListQuery query,
        CancellationToken cancellationToken = default)
    {
        var builder = Builders<WebService>.Filter;
        var filter = WebServiceFilters.VisibleToTenant(query.TenantId);
        if (!string.IsNullOrWhiteSpace(query.ResourceGroupId))
            filter &= builder.Eq(item => item.ResourceGroupId, query.ResourceGroupId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var name = builder.Regex(
                item => item.Name,
                new BsonRegularExpression($".*{Regex.Escape(query.Search)}.*", "i"));
            filter &= ObjectId.TryParse(query.Search, out _)
                ? builder.Or(name, builder.Eq(item => item.Id, query.Search))
                : name;
        }

        var count = await webServices.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken);
        var items = await webServices.Find(filter)
            .Skip(query.Skip)
            .Limit(query.PageSize)
            .ToListAsync(cancellationToken);
        return new WebServicePage(items, count);
    }
}
