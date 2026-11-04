using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed class MongoDeleteHostnameBindingsForResource(
    MongoHostnameBindingCleanup cleanup)
{
    public Task<CleanupHostnameBindingsOutcome> InvokeAsync(
        DeleteHostnameBindingsForResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        return cleanup.DeleteAsync(
            request.TenantId,
            DomainFilters.BindingsForResource(request.TenantId, request.ResourceType, request.ResourceId),
            session,
            cancellationToken);
    }
}
