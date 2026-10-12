using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Dns;
using Pole.Features.Outbox.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.Tenants.Contracts;
using Pole.ResultType;

namespace Pole.Features.Domains.Application.Persistence.DomainZones;

internal sealed class MongoGetOrCreateGeneratedDomainZones(
    IMongoDatabase database,
    ApplyDeploymentTargetReferenceChanges applyDeploymentTargetReferenceChanges,
    FenceDeploymentTargetWorkloadConfigurations fenceDeploymentTargetWorkloadConfigurations,
    LoadTenantIdentity loadTenantIdentity,
    TimeProvider timeProvider,
    StoreOutboxWrites outbox)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);

    public async Task<GetOrCreateGeneratedDomainZonesOutcome> InvokeAsync(
        IReadOnlyList<GetOrCreateGeneratedZoneRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        EnsureRequestSet(requests);

        var tenantId = requests[0].TenantId;
        var identity = await loadTenantIdentity(tenantId, cancellationToken);
        var tenant = identity switch
        {
            TenantIdentitySnapshot success => success,
            TenantIdentityNotFound => null
        };
        if (tenant is null)
        {
            return new GeneratedDomainZoneError(
                    new GeneratedDomainZoneTenantUnavailable(tenantId));
        }

        var candidates = requests
            .Select(request => CreateGeneratedZone(request, tenant.DisplayName))
            .ToArray();
        var targetIds = requests.Select(request => request.DeploymentTargetId).ToArray();
        var existing = await zones.Find(
                session,
                DomainFilters.GeneratedZonesForTargets(tenantId, targetIds))
            .ToListAsync(cancellationToken);
        var existingByTarget = existing.ToDictionary(
            zone => CanonicalObjectId(zone.DeploymentTargetId),
            StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (existingByTarget.TryGetValue(
                    CanonicalObjectId(candidate.DeploymentTargetId),
                    out var persisted) &&
                !string.Equals(candidate.Domain, persisted.Domain, StringComparison.Ordinal))
            {
                return new GeneratedDomainZoneError(
                        new GeneratedDomainZoneMismatch(candidate.DeploymentTargetId));
            }
        }

        var missing = candidates
            .Where(candidate => !existingByTarget.ContainsKey(
                CanonicalObjectId(candidate.DeploymentTargetId)))
            .ToArray();
        var requestsByTarget = requests.ToDictionary(
            request => CanonicalObjectId(request.DeploymentTargetId),
            StringComparer.Ordinal);
        if (missing.Length > 0)
        {
            var fence = await fenceDeploymentTargetWorkloadConfigurations(
                missing
                    .Select(candidate =>
                    {
                        var request = requestsByTarget[
                            CanonicalObjectId(candidate.DeploymentTargetId)];
                        return new DeploymentTargetWorkloadConfigurationFenceRequest(
                            candidate.DeploymentTargetId,
                            request.ExpectedWorkloadConfigurationRevision);
                    })
                    .ToArray(),
                session,
                cancellationToken);
            if (fence is DeploymentTargetWorkloadConfigurationFenceRejected)
            {
                return new GeneratedDomainZoneError(
                        new GeneratedDomainZoneTargetChanged(
                            missing.Select(candidate => candidate.DeploymentTargetId).ToArray()));
            }

            try
            {
                await zones.InsertManyAsync(
                    session,
                    missing,
                    new InsertManyOptions { IsOrdered = true },
                    cancellationToken);
            }
            catch (MongoWriteException exception) when (
                exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                return GeneratedZoneConflict(missing);
            }
            catch (MongoBulkWriteException<TenantDomainZone> exception) when (
                exception.WriteErrors.Any(error => error.Category == ServerErrorCategory.DuplicateKey))
            {
                return GeneratedZoneConflict(missing);
            }

            foreach (var candidate in missing)
            {
                var registered = await applyDeploymentTargetReferenceChanges(
                    new DeploymentTargetReferenceOwner(
                        DeploymentTargetReferenceOwnerTypes.DomainZone,
                        candidate.Id),
                    [candidate.DeploymentTargetId],
                    [],
                    session,
                    cancellationToken);
                if (registered is DeploymentTargetReferenceChangeRejected)
                {
                    return new GeneratedDomainZoneError(
                            new GeneratedDomainZoneTargetChanged(
                                [candidate.DeploymentTargetId]));
                }

                existingByTarget.Add(CanonicalObjectId(candidate.DeploymentTargetId), candidate);
            }
            await outbox(session, missing.Select(zone => OutboxWrite.Create(zone.ToChangedEvent())).ToArray(), cancellationToken);
        }

        return requests
                .Select(request => existingByTarget[
                    CanonicalObjectId(request.DeploymentTargetId)].ToSnapshot())
                .ToArray();
    }

    private TenantDomainZone CreateGeneratedZone(
        GetOrCreateGeneratedZoneRequest request,
        string tenantDisplayName) =>
        TenantDomainZoneFactory.CreateGenerated(
            request.TenantId,
            request.DeploymentTargetId,
            GeneratedZoneLabel(tenantDisplayName, request.TenantId),
            DnsName.Create(request.PublicBaseDomain),
            new TenantDomainZoneFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));

    private static GeneratedDomainZoneError GeneratedZoneConflict(
        IReadOnlyList<TenantDomainZone> zones) =>
        new GeneratedDomainZoneConflict(
                zones.Select(zone => zone.DeploymentTargetId).ToArray());

    private static string GeneratedZoneLabel(string displayName, string tenantId)
    {
        if (!ObjectId.TryParse(tenantId, out _))
            throw new ArgumentException("A MongoDB ObjectId is required.", nameof(tenantId));

        var slug = new string(displayName.Trim().ToLowerInvariant()
            .Select(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'
                ? character
                : '-')
            .ToArray())
            .Trim('-');
        slug = string.IsNullOrEmpty(slug) ? "tenant" : slug;
        const int tenantIdFragmentWithSeparatorLength = 9;
        var maximumSlugLength = DnsLabel.MaximumLength -
            TenantDomainZoneLabelPolicy.GeneratedPrefix.Length -
            tenantIdFragmentWithSeparatorLength;
        return $"{TenantDomainZoneLabelPolicy.GeneratedPrefix}{slug[..Math.Min(slug.Length, maximumSlugLength)].TrimEnd('-')}-{tenantId[..8]}";
    }

    private static void EnsureRequestSet(IReadOnlyList<GetOrCreateGeneratedZoneRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > DomainCapabilityLimits.MaximumSetSize)
        {
            throw new ArgumentException(
                $"Between 1 and {DomainCapabilityLimits.MaximumSetSize} generated-zone requests are required.",
                nameof(requests));
        }

        if (requests.Any(request =>
                request is null ||
                !ObjectId.TryParse(request.TenantId, out _) ||
                !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
                request.ExpectedWorkloadConfigurationRevision < 1 ||
                !DnsName.TryCreate(request.PublicBaseDomain, out _)))
        {
            throw new ArgumentException(
                "Each generated-zone request requires valid tenant, target, revision, and public-domain values.",
                nameof(requests));
        }

        var tenantId = CanonicalObjectId(requests[0].TenantId);
        if (requests.Any(request =>
                !string.Equals(
                    CanonicalObjectId(request.TenantId),
                    tenantId,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "All generated-zone requests in a set must belong to one tenant.",
                nameof(requests));
        }

        if (requests
            .GroupBy(
                request => CanonicalObjectId(request.DeploymentTargetId),
                StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            throw new ArgumentException(
                "A generated-zone set may contain each deployment target only once.",
                nameof(requests));
        }
    }

    private static string CanonicalObjectId(string value) => ObjectId.Parse(value).ToString();
}
