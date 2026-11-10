using MongoDB.Driver;
using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Contracts;

namespace Pole.Features.Domains.Application.HostnameBindings;

internal sealed class MongoDeleteHostnameBindingsForResource(
    MongoHostnameBindingCleanup cleanup
)
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
