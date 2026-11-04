using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed class MongoLoadHostnameBindingsForDeployment(IMongoDatabase database)
{
    private readonly IMongoCollection<HostnameBinding> bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    public async Task<IReadOnlyList<HostnameBindingSnapshot>> InvokeAsync(
        LoadHostnameBindingsForDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var persistedBindings = await bindings.Find(
                DomainFilters.BindingForDeployment(
                    request.TenantId,
                    request.ResourceType,
                    request.ResourceId,
                    request.ResourceDeploymentId))
            .SortBy(item => item.Hostname)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return persistedBindings.Select(DomainContractMappings.ToSnapshot).ToArray();
    }
}
