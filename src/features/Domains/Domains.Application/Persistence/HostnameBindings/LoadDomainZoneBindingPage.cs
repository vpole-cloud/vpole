using MongoDB.Driver;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

public sealed record DomainZoneBindingPage(
    IReadOnlyList<HostnameBindingSnapshot> Items,
    long TotalCount);

public delegate Task<DomainZoneBindingPage> LoadDomainZoneBindingPage(
    string tenantId,
    string zoneId,
    int pageNumber,
    int pageSize,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadDomainZoneBindingPage(IMongoDatabase database)
{
    private readonly IMongoCollection<HostnameBinding> bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    public async Task<DomainZoneBindingPage> InvokeAsync(
        string tenantId, string zoneId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        var filter = DomainFilters.BindingsForTenantZone(tenantId, zoneId);
        var total = await bindings.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var items = await bindings.Find(filter).SortBy(item => item.Hostname).ThenBy(item => item.Id)
            .Skip((pageNumber - 1) * pageSize).Limit(pageSize).ToListAsync(cancellationToken);
        return new DomainZoneBindingPage(items.Select(item => item.ToSnapshot()).ToArray(), total);
    }
}
