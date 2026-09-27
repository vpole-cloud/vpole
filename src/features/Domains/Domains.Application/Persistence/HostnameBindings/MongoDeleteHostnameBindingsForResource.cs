using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed class MongoDeleteHostnameBindingsForResource(
    MongoHostnameBindingCleanup cleanup)
{
    public Task<CleanupHostnameBindingsOutcome> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        return cleanup.DeleteAsync(
            tenantId,
            DomainFilters.BindingsForResource(tenantId, resourceType, resourceId),
            session,
            cancellationToken);
    }
}
