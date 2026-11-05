using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application;
using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.HostnameBindings;

internal sealed class MongoDeleteHostnameBindingsForDeployment(
    MongoHostnameBindingCleanup cleanup)
{
    public Task<CleanupHostnameBindingsOutcome> InvokeAsync(
        DeleteHostnameBindingsForDeploymentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        return cleanup.DeleteAsync(
            request.TenantId,
            DomainFilters.BindingForDeployment(
                request.TenantId,
                request.ResourceType,
                request.ResourceId,
                request.ResourceDeploymentId),
            session,
            cancellationToken);
    }
}
