using MongoDB.Driver;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.HostnameBindings;

internal sealed class MongoHostnameBindingReferences(
    IMongoCollection<HostnameBinding> bindings
)
{
    public async Task<NamedHostnameBindingOutcome> ResolveAsync(
        NamedHostnameBindingRequest request,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var filter = DomainFilters.BindingByTechnicalName(request.TenantId, request.TechnicalName);
        var binding = await (session is null ? bindings.Find(filter) : bindings.Find(session, filter))
            .FirstOrDefaultAsync(cancellationToken);
        return binding is null
            ? new NamedHostnameBindingNotFound(request.TechnicalName)
            : new NamedHostnameBindingFound(binding.Id);
    }

    public async Task<HostnameBindingPlanningSnapshot?> LoadAsync(
        LoadHostnameBindingPlanningRequest request,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var filter = DomainFilters.BindingById(request.TenantId, request.Id);
        var binding = await (session is null ? bindings.Find(filter) : bindings.Find(session, filter))
            .FirstOrDefaultAsync(cancellationToken);
        return binding is null
            ? null
            : new HostnameBindingPlanningSnapshot(binding.ToSnapshot(), binding.ReferenceFence);
    }

    public async Task<FenceHostnameBindingOutcome> FenceAsync(
        FenceHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        DomainTransactions.EnsureActive(session);
        var filter = DomainFilters.BindingAtPlanningVersion(request);
        var updated = await bindings.UpdateOneAsync(
            session,
            filter,
            Builders<HostnameBinding>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        return updated.ModifiedCount == 1
            ? new HostnameBindingFenced()
            : new HostnameBindingPreconditionChanged(request.Id);
    }
}
