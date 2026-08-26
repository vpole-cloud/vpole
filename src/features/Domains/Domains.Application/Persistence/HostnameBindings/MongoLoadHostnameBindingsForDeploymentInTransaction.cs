using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed class MongoLoadHostnameBindingsForDeploymentInTransaction(IMongoDatabase database)
{
    private readonly IMongoCollection<HostnameBinding> bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    public async Task<IReadOnlyList<HostnameBindingSnapshot>> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        var persistedBindings = await bindings.Find(
                session,
                DomainFilters.BindingForDeployment(
                    tenantId,
                    resourceType,
                    resourceId,
                    resourceDeploymentId))
            .SortBy(item => item.Hostname)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return persistedBindings.Select(DomainContractMappings.ToSnapshot).ToArray();
    }
}
