using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed class MongoDeleteHostnameBindingsForDeployment(
    MongoHostnameBindingCleanup cleanup)
{
    public Task<Result<Unit, HostnameBindingCleanupError>> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        return cleanup.DeleteAsync(
            tenantId,
            DomainFilters.BindingForDeployment(
                tenantId,
                resourceType,
                resourceId,
                resourceDeploymentId),
            session,
            cancellationToken);
    }
}
