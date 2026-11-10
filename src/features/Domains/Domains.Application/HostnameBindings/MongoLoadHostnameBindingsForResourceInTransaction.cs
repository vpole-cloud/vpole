using MongoDB.Driver;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.HostnameBindings;

internal sealed class MongoLoadHostnameBindingsForResourceInTransaction(
    IMongoCollection<HostnameBinding> bindings
)
{
    public async Task<IReadOnlyList<HostnameBindingSnapshot>> InvokeAsync(
        LoadHostnameBindingsForResourceInTransactionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        DomainTransactions.EnsureActive(session);
        var current = await bindings
            .Find(
                session,
                DomainFilters.BindingsForResource(request.TenantId, request.ResourceType, request.ResourceId))
            .SortBy(item => item.Hostname)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return current.Select(DomainContractMappings.ToSnapshot).ToArray();
    }
}
