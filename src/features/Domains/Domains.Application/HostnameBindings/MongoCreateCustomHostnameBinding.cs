using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Dns;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.HostnameBindings;

internal sealed class MongoCreateCustomHostnameBinding(
    IMongoCollection<TenantDomainZone> zones,
    IMongoCollection<HostnameBinding> bindings,
    MongoHostnameBindingCleanup cleanup,
    TimeProvider timeProvider,
    StoreOutboxWrites outbox)
{
    public async Task<CreateCustomHostnameBindingOutcome> InvokeAsync(
        CreateCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureRequest(request);
        DomainTransactions.EnsureActive(session);
        var zone = await zones.Find(
                session,
                DomainFilters.ZoneById(request.TenantId, request.TenantDomainZoneId))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
        {
            return new CustomHostnameBindingError(
                new CustomHostnameBindingZoneNotFound(request.TenantDomainZoneId));
        }

        if (!string.Equals(
                zone.DeploymentTargetId,
                request.DeploymentTargetId,
                StringComparison.Ordinal))
        {
            return new CustomHostnameBindingError(
                new CustomHostnameBindingTargetMismatch(
                    zone.Id,
                    request.DeploymentTargetId));
        }

        var zoneDomain = DnsName.Create(zone.Domain);
        try
        {
            RelativeDnsName.Create(request.RelativeName).Under(zoneDomain);
        }
        catch (ArgumentException)
        {
            return new CustomHostnameBindingError(new CustomHostnameBindingInvalidRelativeName());
        }

        var binding = HostnameBindingFactory.CreateCustom(
            request.TenantId,
            zone.Id,
            request.DeploymentTargetId,
            request.ResourceType,
            request.ResourceId,
            request.ResourceDeploymentId,
            request.RelativeName,
            zoneDomain,
            request.TechnicalName,
            new HostnameBindingFactory.CreateOptions(
                request.BindingId,
                timeProvider.GetUtcNow().UtcDateTime));

        if (!await cleanup.TryAdvanceZoneRevisionsAsync([zone], session, cancellationToken))
        {
            return new CustomHostnameBindingError(
                new CustomHostnameBindingZoneChanged([zone.Id]));
        }

        try
        {
            await bindings.InsertOneAsync(session, binding, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
            exception, DomainIndexNames.BindingTechnicalName, bindings.CollectionNamespace.CollectionName))
        {
            return new CustomHostnameBindingError(new CustomHostnameBindingNameConflict(request.TechnicalName));
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
            exception, DomainIndexNames.BindingHostname, bindings.CollectionNamespace.CollectionName))
        {
            return new CustomHostnameBindingError(
                new CustomHostnameBindingConflict(binding.Hostname));
        }

        await outbox(session,
            [OutboxWrite.Create(cleanup.WithAdvancedRevision(zone).ToChangedEvent(binding.Id))],
            cancellationToken);
        return binding.ToSnapshot();
    }

    private static void EnsureRequest(CreateCustomHostnameBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ObjectId.TryParse(request.BindingId, out _) ||
            !ObjectId.TryParse(request.TenantId, out _) ||
            !ObjectId.TryParse(request.TenantDomainZoneId, out _) ||
            !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
            !ObjectId.TryParse(request.ResourceId, out _) ||
            !ObjectId.TryParse(request.ResourceDeploymentId, out _) ||
            string.IsNullOrWhiteSpace(request.ResourceType.Value) ||
            string.IsNullOrWhiteSpace(request.RelativeName))
        {
            throw new ArgumentException(
                "A custom hostname binding request must contain valid resource identifiers and a relative name.",
                nameof(request));
        }
    }
}
