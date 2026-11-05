using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.HostnameBindings;

internal sealed class MongoLoadHostnameBindingsForDeployment(IMongoCollection<HostnameBinding> bindings)
{
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
