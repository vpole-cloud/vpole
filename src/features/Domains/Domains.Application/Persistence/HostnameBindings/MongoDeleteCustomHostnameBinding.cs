using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed record RemoveCustomHostnameBindingRequest(
    DeleteCustomHostnameBindingRequest Binding,
    GitLifecycleAuthority? Lifecycle);

internal sealed class MongoDeleteCustomHostnameBinding(
    IMongoDatabase database,
    MongoHostnameBindingCleanup cleanup,
    StoreOutboxWrites outbox,
    IGitOpsMutations git)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);

    private readonly IMongoCollection<HostnameBinding> bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    public Task<DeleteCustomHostnameBindingOutcome> InvokeAsync(
        DeleteCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) => ExecuteAsync(new(request, null), session, cancellationToken);

    internal async Task<DeleteCustomHostnameBindingOutcome> ExecuteAsync(
        RemoveCustomHostnameBindingRequest operation,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var request = operation.Binding;
        EnsureRequest(request);
        DomainTransactions.EnsureActive(session);
        var filter = DomainFilters.BindingById(request);
        var binding = await bindings.Find(session, filter).FirstOrDefaultAsync(cancellationToken);
        if (binding is null)
        {
            return new CustomHostnameBindingError(
                new CustomHostnameBindingNotFound(request.BindingId));
        }

        if (!await git.AuthorizeAsync(new(request.TenantId, "HostnameBinding", binding.Id,
                Lifecycle: operation.Lifecycle,
                LifecycleEffect: GitLifecycleMutationEffect.Remove),
                session, cancellationToken))
            return new CustomHostnameBindingGitOwned(binding.Id);

        if (binding.Kind == HostnameBindingKind.Generated)
        {
            return new CustomHostnameBindingError(
                new GeneratedHostnameBindingDeletionRejected(binding.Id));
        }

        var zone = await zones.Find(
                session,
                DomainFilters.ZoneById(request.TenantId, binding.TenantDomainZoneId))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
        {
            return new CustomHostnameBindingError(
                new CustomHostnameBindingZoneUnavailable(binding.TenantDomainZoneId));
        }

        if (!await cleanup.TryAdvanceZoneRevisionsAsync([zone], session, cancellationToken))
        {
            return new CustomHostnameBindingError(
                new CustomHostnameBindingZoneChanged([zone.Id]));
        }

        var deleted = await bindings.DeleteOneAsync(session, filter, cancellationToken: cancellationToken);
        if (deleted.DeletedCount != 1)
        {
            return new CustomHostnameBindingError(
                new CustomHostnameBindingDeletionConflict(binding.Id));
        }

        var released = await cleanup.ReleaseGeneratedZonesWithoutBindingsAsync(
            [cleanup.WithAdvancedRevision(zone)],
            session,
            cancellationToken);
        if (released is HostnameBindingsCleanedUp)
            await outbox(session,
                [OutboxWrite.Create(cleanup.WithAdvancedRevision(zone).ToChangedEvent(binding.Id))],
                cancellationToken);
        return released switch
        {
            HostnameBindingsCleanedUp => new CustomHostnameBindingDeleted(),
            HostnameBindingCleanupError => new CustomHostnameBindingError(
                new CustomHostnameBindingZoneChanged([zone.Id]))
        };
    }

    private static void EnsureRequest(DeleteCustomHostnameBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ObjectId.TryParse(request.TenantId, out _) ||
            !ObjectId.TryParse(request.ResourceId, out _) ||
            !ObjectId.TryParse(request.ResourceDeploymentId, out _) ||
            !ObjectId.TryParse(request.BindingId, out _) ||
            string.IsNullOrWhiteSpace(request.ResourceType.Value))
        {
            throw new ArgumentException(
                "A custom hostname binding deletion request must contain valid resource identifiers.",
                nameof(request));
        }
    }
}
