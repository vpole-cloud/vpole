using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.ResultType;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

/// <summary>
/// Owns the shared transactional lifecycle for hostname bindings and their generated zones.
/// </summary>
internal sealed class MongoHostnameBindingCleanup(
    IMongoDatabase database,
    ApplyDeploymentTargetReferenceChanges applyDeploymentTargetReferenceChanges,
    TimeProvider timeProvider,
    StoreOutboxWrites outbox)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);
    private readonly IMongoCollection<HostnameBinding> bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    internal async Task<CleanupHostnameBindingsOutcome> DeleteAsync(
        string tenantId,
        FilterDefinition<HostnameBinding> bindingFilter,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var persistedBindings = await bindings.Find(session, bindingFilter)
            .ToListAsync(cancellationToken);
        if (persistedBindings.Count == 0)
            return new HostnameBindingsCleanedUp();

        var zoneIds = persistedBindings
            .Select(binding => binding.TenantDomainZoneId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var persistedZones = await zones.Find(
                session,
                DomainFilters.ZonesById(tenantId, zoneIds))
            .ToListAsync(cancellationToken);
        if (persistedZones.Count != zoneIds.Length)
        {
            return new HostnameBindingCleanupError(
                    new HostnameBindingCleanupZoneUnavailable(zoneIds));
        }

        if (!await TryAdvanceZoneRevisionsAsync(persistedZones, session, cancellationToken))
        {
            return new HostnameBindingCleanupError(
                    new HostnameBindingCleanupZoneChanged(zoneIds));
        }

        var deleted = await bindings.DeleteManyAsync(
            session,
            bindingFilter,
            cancellationToken: cancellationToken);
        if (!deleted.IsAcknowledged || deleted.DeletedCount != persistedBindings.Count)
        {
            return new HostnameBindingCleanupError(
                    new HostnameBindingCleanupBindingsChanged());
        }

        var result = await ReleaseGeneratedZonesWithoutBindingsAsync(
            persistedZones.Select(WithAdvancedRevision).ToArray(),
            session,
            cancellationToken);
        if (result is HostnameBindingsCleanedUp)
            await outbox(session, persistedBindings.Select(binding => OutboxWrite.Create(WithAdvancedRevision(
                persistedZones.Single(zone => zone.Id == binding.TenantDomainZoneId)).ToChangedEvent(binding.Id))).ToArray(), cancellationToken);
        return result;
    }

    internal async Task<bool> TryAdvanceZoneRevisionsAsync(
        IReadOnlyCollection<TenantDomainZone> candidateZones,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var distinctZones = candidateZones
            .DistinctBy(zone => zone.Id, StringComparer.Ordinal)
            .ToArray();
        if (distinctZones.Length == 0)
            return true;
        if (distinctZones.Any(zone => zone.Revision == long.MaxValue))
        {
            throw new InvalidOperationException(
                "A domain zone revision cannot be advanced further.");
        }

        var update = await zones.UpdateManyAsync(
            session,
            DomainFilters.AtRevisions(distinctZones),
            Builders<TenantDomainZone>.Update.Combine(
                Builders<TenantDomainZone>.Update.Inc(item => item.Revision, 1),
                Builders<TenantDomainZone>.Update.Set(
                    item => item.UpdatedAtUtc,
                    timeProvider.GetUtcNow().UtcDateTime)),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == distinctZones.Length;
    }

    internal async Task<CleanupHostnameBindingsOutcome>
        ReleaseGeneratedZonesWithoutBindingsAsync(
            IReadOnlyCollection<TenantDomainZone> candidateZones,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
    {
        var generatedZones = candidateZones
            .Where(zone => zone.Kind == TenantDomainZoneKind.Generated)
            .DistinctBy(zone => zone.Id, StringComparer.Ordinal)
            .ToArray();
        if (generatedZones.Length == 0)
            return new HostnameBindingsCleanedUp();

        var generatedZoneIds = generatedZones.Select(zone => zone.Id).ToArray();
        var referencedZoneIds = await bindings.Find(
                session,
                DomainFilters.BindingsForZones(generatedZoneIds))
            .Project(binding => binding.TenantDomainZoneId)
            .ToListAsync(cancellationToken);
        var referenced = referencedZoneIds.ToHashSet(StringComparer.Ordinal);
        var releasable = generatedZones
            .Where(zone => !referenced.Contains(zone.Id))
            .ToArray();
        if (releasable.Length == 0)
            return new HostnameBindingsCleanedUp();

        var deletion = await zones.DeleteManyAsync(
            session,
            DomainFilters.GeneratedAtRevisions(releasable),
            cancellationToken: cancellationToken);
        if (deletion.DeletedCount != releasable.Length)
        {
            return new HostnameBindingCleanupError(
                    new HostnameBindingCleanupZoneChanged(
                        releasable.Select(zone => zone.Id).ToArray()));
        }

        foreach (var zone in releasable)
        {
            var unregistered = await applyDeploymentTargetReferenceChanges(
                new DeploymentTargetReferenceOwner(
                    DeploymentTargetReferenceOwnerTypes.DomainZone,
                    zone.Id),
                [],
                [zone.DeploymentTargetId],
                session,
                cancellationToken);
            if (unregistered is DeploymentTargetReferenceChangeRejected)
            {
                return new HostnameBindingCleanupError(
                        new HostnameBindingCleanupReferenceRemovalRejected(
                            zone.Id,
                            zone.DeploymentTargetId));
            }
        }
        await outbox(session, releasable.Select(zone => OutboxWrite.Create(zone.ToChangedEvent())).ToArray(), cancellationToken);

        return new HostnameBindingsCleanedUp();
    }

    internal TenantDomainZone WithAdvancedRevision(TenantDomainZone zone) =>
        zone with
        {
            Revision = checked(zone.Revision + 1),
            UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };
}
