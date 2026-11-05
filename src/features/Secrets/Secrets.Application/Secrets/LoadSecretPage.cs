using MongoDB.Driver;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Secrets;

public delegate Task<SecretPageData> LoadSecretPage(
    string tenantId,
    string? after,
    int pageSize,
    CancellationToken ct);

public sealed record SecretPageData(
    Secret[] Items,
    string? NextCursor);

internal sealed class MongoLoadSecretPage(
    IMongoCollection<Secret> secrets)
{
    public async Task<SecretPageData> InvokeAsync(
        string tenantId,
        string? after,
        int pageSize,
        CancellationToken ct)
    {
        var filter = SecretFilters.Page(tenantId, after);
        var page = await secrets.Find(filter)
            .SortBy(x => x.Id)
            .Limit(pageSize + 1)
            .ToListAsync(ct);
        var items = page.Take(pageSize).ToArray();
        return new SecretPageData(items, page.Count > pageSize ? items[^1].Id : null);
    }
}
