using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Dns;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed class MongoCreateGeneratedHostnameBindings(
    IMongoDatabase database,
    MongoHostnameBindingCleanup cleanup,
    TimeProvider timeProvider,
    StoreOutboxWrites outbox)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);
    private readonly IMongoCollection<HostnameBinding> bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    public async Task<CreateGeneratedHostnameBindingsOutcome> InvokeAsync(
        IReadOnlyList<CreateGeneratedHostnameBindingRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        EnsureRequestSet(requests);

        var tenantId = requests[0].TenantId;
        var zoneIds = requests
            .Select(request => request.TenantDomainZoneId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var persistedZones = await zones.Find(
                session,
                DomainFilters.ZonesById(tenantId, zoneIds))
            .ToListAsync(cancellationToken);
        var zonesById = persistedZones.ToDictionary(
            zone => CanonicalObjectId(zone.Id),
            StringComparer.Ordinal);
        if (zoneIds.Any(zoneId => !zonesById.ContainsKey(CanonicalObjectId(zoneId))))
        {
            var missingZoneId = zoneIds.First(
                zoneId => !zonesById.ContainsKey(CanonicalObjectId(zoneId)));
            return new GeneratedHostnameBindingError(
                    new GeneratedHostnameBindingZoneNotFound(missingZoneId));
        }

        var candidates = new List<HostnameBinding>(requests.Count);
        foreach (var request in requests)
        {
            var zone = zonesById[CanonicalObjectId(request.TenantDomainZoneId)];
            if (!string.Equals(
                    zone.DeploymentTargetId,
                    request.DeploymentTargetId,
                    StringComparison.Ordinal))
            {
                return new GeneratedHostnameBindingError(
                        new GeneratedHostnameBindingTargetMismatch(
                            zone.Id,
                            request.DeploymentTargetId));
            }

            candidates.Add(HostnameBindingFactory.CreateGenerated(
                request.TenantId,
                zone.Id,
                request.DeploymentTargetId,
                request.ResourceType,
                request.ResourceId,
                request.ResourceDeploymentId,
                request.RelativeName,
                DnsName.Create(zone.Domain),
                new HostnameBindingFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(),
                    timeProvider.GetUtcNow().UtcDateTime)));
        }

        if (candidates
            .GroupBy(binding => binding.Hostname, StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            throw new ArgumentException(
                "Generated hostnames must be unique within a binding set.",
                nameof(requests));
        }

        var deploymentIds = requests
            .Select(request => request.ResourceDeploymentId)
            .ToArray();
        var hostnames = candidates.Select(binding => binding.Hostname).ToArray();
        var existingConflict = await bindings.Find(
                session,
                DomainFilters.GeneratedBindingConflicts(
                    tenantId,
                    hostnames,
                    deploymentIds))
            .Limit(1)
            .AnyAsync(cancellationToken);
        if (existingConflict)
            return new GeneratedHostnameBindingError(new GeneratedHostnameBindingConflict(hostnames));

        if (!await cleanup.TryAdvanceZoneRevisionsAsync(
                persistedZones,
                session,
                cancellationToken))
        {
            return new GeneratedHostnameBindingError(
                    new GeneratedHostnameBindingZoneChanged(
                        persistedZones.Select(zone => zone.Id).ToArray()));
        }

        try
        {
            await bindings.InsertManyAsync(
                session,
                candidates,
                new InsertManyOptions { IsOrdered = true },
                cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new GeneratedHostnameBindingError(new GeneratedHostnameBindingConflict(hostnames));
        }
        catch (MongoBulkWriteException<HostnameBinding> exception) when (
            exception.WriteErrors.Any(error => error.Category == ServerErrorCategory.DuplicateKey))
        {
            return new GeneratedHostnameBindingError(new GeneratedHostnameBindingConflict(hostnames));
        }

        await outbox(session, candidates.Select(binding => OutboxWrite.Create(
            cleanup.WithAdvancedRevision(zonesById[CanonicalObjectId(binding.TenantDomainZoneId)]).ToChangedEvent(binding.Id))).ToArray(), cancellationToken);
        return candidates.Select(DomainContractMappings.ToSnapshot).ToArray();
    }

    private static void EnsureRequestSet(
        IReadOnlyList<CreateGeneratedHostnameBindingRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > DomainCapabilityLimits.MaximumSetSize)
        {
            throw new ArgumentException(
                $"Between 1 and {DomainCapabilityLimits.MaximumSetSize} generated-binding requests are required.",
                nameof(requests));
        }

        if (requests.Any(request =>
                request is null ||
                !ObjectId.TryParse(request.TenantId, out _) ||
                !ObjectId.TryParse(request.TenantDomainZoneId, out _) ||
                !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
                !ObjectId.TryParse(request.ResourceId, out _) ||
                !ObjectId.TryParse(request.ResourceDeploymentId, out _) ||
                string.IsNullOrWhiteSpace(request.ResourceType.Value) ||
                !IsValidRelativeDnsName(request.RelativeName)))
        {
            throw new ArgumentException(
                "Each generated-binding request requires valid tenant, zone, target, resource, deployment, and relative-name values.",
                nameof(requests));
        }

        var first = requests[0];
        var tenantId = CanonicalObjectId(first.TenantId);
        var resourceId = CanonicalObjectId(first.ResourceId);
        if (requests.Any(request =>
                !string.Equals(CanonicalObjectId(request.TenantId), tenantId, StringComparison.Ordinal) ||
                !string.Equals(request.ResourceType.Value, first.ResourceType.Value, StringComparison.Ordinal) ||
                !string.Equals(CanonicalObjectId(request.ResourceId), resourceId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "All generated bindings in a set must belong to one tenant and logical resource.",
                nameof(requests));
        }

        if (requests
            .GroupBy(
                request => CanonicalObjectId(request.ResourceDeploymentId),
                StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            throw new ArgumentException(
                "A generated-binding set may contain each resource deployment only once.",
                nameof(requests));
        }
    }

    private static bool IsValidRelativeDnsName(string? value)
    {
        try
        {
            _ = RelativeDnsName.Create(value ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string CanonicalObjectId(string value) => ObjectId.Parse(value).ToString();
}
