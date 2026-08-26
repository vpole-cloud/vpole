using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.Tenants.Contracts;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.Persistence;

internal sealed partial class MongoDomainPersistence(
    IMongoDatabase database,
    ApplyDeploymentTargetReferenceChanges applyDeploymentTargetReferenceChanges,
    FenceDeploymentTargetWorkloadConfigurations fenceDeploymentTargetWorkloadConfigurations,
    LoadTenantIdentity loadTenantIdentity,
    TimeProvider timeProvider)
{
    private readonly IMongoCollection<TenantDomainZone> _zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);
    private readonly IMongoCollection<HostnameBinding> _bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    private async Task<Result<Unit, HostnameBindingCleanupError>>
        DeleteBindingsAndReleaseGeneratedZonesAsync(
        string tenantId,
        FilterDefinition<HostnameBinding> bindingFilter,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var bindings = await _bindings.Find(session, bindingFilter)
            .ToListAsync(cancellationToken);
        if (bindings.Count == 0)
            return new Ok<Unit>(Unit.Value);

        var zoneIds = bindings
            .Select(binding => binding.TenantDomainZoneId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var zones = await _zones.Find(
                session,
                DomainFilters.ZonesById(tenantId, zoneIds))
            .ToListAsync(cancellationToken);
        if (zones.Count != zoneIds.Length)
        {
            return new Err<HostnameBindingCleanupError>(
                new HostnameBindingCleanupError(
                    new HostnameBindingCleanupZoneUnavailable(zoneIds)));
        }

        if (!await TryAdvanceZoneRevisionsAsync(
            zones,
            session,
            cancellationToken))
        {
            return new Err<HostnameBindingCleanupError>(
                new HostnameBindingCleanupError(
                    new HostnameBindingCleanupZoneChanged(zoneIds)));
        }

        var deleted = await _bindings.DeleteManyAsync(
            session,
            bindingFilter,
            cancellationToken: cancellationToken);
        if (!deleted.IsAcknowledged || deleted.DeletedCount != bindings.Count)
        {
            return new Err<HostnameBindingCleanupError>(
                new HostnameBindingCleanupError(
                    new HostnameBindingCleanupBindingsChanged()));
        }

        return await ReleaseGeneratedZonesWithoutBindingsAsync(
            zones.Select(zone => zone with
            {
                Revision = checked(zone.Revision + 1),
                UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
            }).ToArray(),
            session,
            cancellationToken);
    }

    private async Task<bool> TryAdvanceZoneRevisionsAsync(
        IReadOnlyCollection<TenantDomainZone> zones,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var distinctZones = zones
            .DistinctBy(zone => zone.Id, StringComparer.Ordinal)
            .ToArray();
        if (distinctZones.Length == 0)
            return true;
        if (distinctZones.Any(zone => zone.Revision == long.MaxValue))
        {
            throw new InvalidOperationException(
                "A domain zone revision cannot be advanced further.");
        }

        var update = await _zones.UpdateManyAsync(
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

    private async Task<Result<Unit, HostnameBindingCleanupError>>
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
            return new Ok<Unit>(Unit.Value);

        var generatedZoneIds = generatedZones.Select(zone => zone.Id).ToArray();
        var referencedZoneIds = await _bindings.Find(
                session,
                DomainFilters.BindingsForZones(generatedZoneIds))
            .Project(binding => binding.TenantDomainZoneId)
            .ToListAsync(cancellationToken);
        var referenced = referencedZoneIds.ToHashSet(StringComparer.Ordinal);
        var releasable = generatedZones
            .Where(zone => !referenced.Contains(zone.Id))
            .ToArray();
        if (releasable.Length == 0)
            return new Ok<Unit>(Unit.Value);

        var deletion = await _zones.DeleteManyAsync(
            session,
            DomainFilters.GeneratedAtRevisions(releasable),
            cancellationToken: cancellationToken);
        if (deletion.DeletedCount != releasable.Length)
        {
            return new Err<HostnameBindingCleanupError>(
                new HostnameBindingCleanupError(
                    new HostnameBindingCleanupZoneChanged(
                        releasable.Select(zone => zone.Id).ToArray())));
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
            if (unregistered is Err<DeploymentTargetReferenceChangeRejected>)
            {
                return new Err<HostnameBindingCleanupError>(
                    new HostnameBindingCleanupError(
                        new HostnameBindingCleanupReferenceRemovalRejected(
                            zone.Id,
                            zone.DeploymentTargetId)));
            }
        }

        return new Ok<Unit>(Unit.Value);
    }

    private TenantDomainZone CreateGeneratedZone(
        GetOrCreateGeneratedZoneRequest request,
        string tenantDisplayName) =>
        TenantDomainZoneFactory.CreateGenerated(
            request.TenantId,
            request.DeploymentTargetId,
            GeneratedZoneLabel(tenantDisplayName, request.TenantId),
            DnsName.Create(request.PublicBaseDomain),
            CreateOptions());

    private TenantDomainZoneFactory.CreateOptions CreateOptions() =>
        new(ObjectId.GenerateNewId().ToString(), timeProvider.GetUtcNow().UtcDateTime);

    private HostnameBindingFactory.CreateOptions BindingCreateOptions() =>
        new(ObjectId.GenerateNewId().ToString(), timeProvider.GetUtcNow().UtcDateTime);

    private static string GeneratedZoneLabel(string displayName, string tenantId)
    {
        if (!ObjectId.TryParse(tenantId, out _))
            throw new ArgumentException("A MongoDB ObjectId is required.", nameof(tenantId));
        var slug = new string(displayName.Trim().ToLowerInvariant()
            .Select(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' ? character : '-')
            .ToArray())
            .Trim('-');
        slug = string.IsNullOrEmpty(slug) ? "tenant" : slug;
        const int tenantIdFragmentWithSeparatorLength = 9;
        var maximumSlugLength = DnsLabel.MaximumLength -
            TenantDomainZoneLabelPolicy.GeneratedPrefix.Length -
            tenantIdFragmentWithSeparatorLength;
        return $"{TenantDomainZoneLabelPolicy.GeneratedPrefix}{slug[..Math.Min(slug.Length, maximumSlugLength)].TrimEnd('-')}-{tenantId[..8]}";
    }

    private static void EnsureGeneratedZoneSet(
        IReadOnlyList<GetOrCreateGeneratedZoneRequest> requests)
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

    private static void EnsureGeneratedBindingSet(
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

    private static string CanonicalObjectId(string value) =>
        ObjectId.Parse(value).ToString();

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

    private static void EnsureCustomCreateRequest(
        CreateCustomHostnameBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ObjectId.TryParse(request.TenantId, out _) ||
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

    private static void EnsureCustomDeleteRequest(
        DeleteCustomHostnameBindingRequest request)
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
