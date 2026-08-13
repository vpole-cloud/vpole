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
    IDeploymentTargetReferenceAdmission targetReferenceAdmission,
    IDeploymentTargetWorkloadConfigurationFence targetWorkloadConfigurationFence,
    ITenantIdentityLookup tenantIdentityLookup,
    TimeProvider timeProvider)
{
    private readonly IMongoCollection<TenantDomainZone> _zones =
        database.GetCollection<TenantDomainZone>("TenantDomainZones");
    private readonly IMongoCollection<HostnameBinding> _bindings =
        database.GetCollection<HostnameBinding>("HostnameBindings");

    private async Task<Result> DeleteBindingsAndReleaseGeneratedZonesAsync(
        string tenantId,
        FilterDefinition<HostnameBinding> bindingFilter,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var bindings = await _bindings.Find(session, bindingFilter)
            .ToListAsync(cancellationToken);
        if (bindings.Count == 0)
            return Result.Success();

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
            return Failures.Conflict(
                "One or more hostname bindings refer to an unavailable domain zone.");
        }

        var advancedZones = await AdvanceZoneRevisionsAsync(
            zones,
            session,
            cancellationToken);
        if (advancedZones is not ISuccess)
            return (Failure)advancedZones;

        var deleted = await _bindings.DeleteManyAsync(
            session,
            bindingFilter,
            cancellationToken: cancellationToken);
        if (!deleted.IsAcknowledged || deleted.DeletedCount != bindings.Count)
            return Failures.Conflict("Hostname bindings changed while deletion was being accepted.");

        return await ReleaseGeneratedZonesWithoutBindingsAsync(
            zones.Select(zone => zone with
            {
                Revision = checked(zone.Revision + 1),
                UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
            }).ToArray(),
            session,
            cancellationToken);
    }

    private async Task<Result> AdvanceZoneRevisionsAsync(
        IReadOnlyCollection<TenantDomainZone> zones,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var distinctZones = zones
            .DistinctBy(zone => zone.Id, StringComparer.Ordinal)
            .ToArray();
        if (distinctZones.Length == 0)
            return Result.Success();
        if (distinctZones.Any(zone => zone.Revision == long.MaxValue))
            return Failures.Conflict("A domain zone revision cannot be advanced further.");

        var update = await _zones.UpdateManyAsync(
            session,
            DomainFilters.AtRevisions(distinctZones),
            Builders<TenantDomainZone>.Update.Combine(
                Builders<TenantDomainZone>.Update.Inc(item => item.Revision, 1),
                Builders<TenantDomainZone>.Update.Set(
                    item => item.UpdatedAtUtc,
                    timeProvider.GetUtcNow().UtcDateTime)),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == distinctZones.Length
            ? Result.Success()
            : Failures.Conflict("A domain zone changed while hostname ownership was being updated.");
    }

    private async Task<Result> ReleaseGeneratedZonesWithoutBindingsAsync(
        IReadOnlyCollection<TenantDomainZone> candidateZones,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var generatedZones = candidateZones
            .Where(zone => zone.Kind == TenantDomainZoneKind.Generated)
            .DistinctBy(zone => zone.Id, StringComparer.Ordinal)
            .ToArray();
        if (generatedZones.Length == 0)
            return Result.Success();

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
            return Result.Success();

        var deletion = await _zones.DeleteManyAsync(
            session,
            DomainFilters.GeneratedAtRevisions(releasable),
            cancellationToken: cancellationToken);
        if (deletion.DeletedCount != releasable.Length)
            return Failures.Conflict("A generated domain zone changed while it was being released.");

        foreach (var zone in releasable)
        {
            var unregistered = await targetReferenceAdmission.UnregisterAsync(
                zone.DeploymentTargetId,
                new DeploymentTargetReferenceOwner(
                    DeploymentTargetReferenceOwnerTypes.DomainZone,
                    zone.Id),
                session,
                cancellationToken);
            if (unregistered is not ISuccess)
                return (Failure)unregistered;
        }

        return Result.Success();
    }

    private bool TryCreateGeneratedZone(
        GetOrCreateGeneratedZoneRequest request,
        string tenantDisplayName,
        out TenantDomainZone? zone,
        out Failure? failure)
    {
        try
        {
            zone = TenantDomainZoneFactory.CreateGenerated(
                request.TenantId,
                request.DeploymentTargetId,
                GeneratedZoneLabel(tenantDisplayName, request.TenantId),
                DnsName.Create(request.PublicBaseDomain),
                CreateOptions());
            failure = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            zone = null;
            failure = Invalid("GeneratedZone", exception.Message);
            return false;
        }
    }

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

    private static ValidationFailure? ValidateGeneratedZoneSet(
        IReadOnlyList<GetOrCreateGeneratedZoneRequest>? requests)
    {
        if (requests is null || requests.Count is < 1 or > DomainCapabilityLimits.MaximumSetSize)
        {
            return Invalid(
                "Requests",
                $"Between 1 and {DomainCapabilityLimits.MaximumSetSize} generated-zone requests are required.");
        }

        if (requests.Any(request =>
                !ObjectId.TryParse(request.TenantId, out _) ||
                !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
                request.ExpectedWorkloadConfigurationRevision < 1 ||
                !DnsName.TryCreate(request.PublicBaseDomain, out _)))
        {
            return Invalid(
                "Requests",
                "Each generated-zone request requires valid tenant, target, revision, and public-domain values.");
        }

        var tenantId = CanonicalObjectId(requests[0].TenantId);
        if (requests.Any(request =>
                !string.Equals(
                    CanonicalObjectId(request.TenantId),
                    tenantId,
                    StringComparison.Ordinal)))
        {
            return Invalid(
                "Requests",
                "All generated-zone requests in a set must belong to one tenant.");
        }

        if (requests
            .GroupBy(
                request => CanonicalObjectId(request.DeploymentTargetId),
                StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            return Invalid(
                "Requests",
                "A generated-zone set may contain each deployment target only once.");
        }

        return null;
    }

    private static ValidationFailure? ValidateGeneratedBindingSet(
        IReadOnlyList<CreateGeneratedHostnameBindingRequest>? requests)
    {
        if (requests is null || requests.Count is < 1 or > DomainCapabilityLimits.MaximumSetSize)
        {
            return Invalid(
                "Requests",
                $"Between 1 and {DomainCapabilityLimits.MaximumSetSize} generated-binding requests are required.");
        }

        if (requests.Any(request =>
                !ObjectId.TryParse(request.TenantId, out _) ||
                !ObjectId.TryParse(request.TenantDomainZoneId, out _) ||
                !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
                !ObjectId.TryParse(request.ResourceId, out _) ||
                !ObjectId.TryParse(request.ResourceDeploymentId, out _) ||
                string.IsNullOrWhiteSpace(request.ResourceType.Value)))
        {
            return Invalid(
                "Requests",
                "Each generated-binding request requires valid tenant, zone, target, resource, and deployment values.");
        }

        var first = requests[0];
        var tenantId = CanonicalObjectId(first.TenantId);
        var resourceId = CanonicalObjectId(first.ResourceId);
        if (requests.Any(request =>
                !string.Equals(CanonicalObjectId(request.TenantId), tenantId, StringComparison.Ordinal) ||
                !string.Equals(request.ResourceType.Value, first.ResourceType.Value, StringComparison.Ordinal) ||
                !string.Equals(CanonicalObjectId(request.ResourceId), resourceId, StringComparison.Ordinal)))
        {
            return Invalid(
                "Requests",
                "All generated bindings in a set must belong to one tenant and logical resource.");
        }

        if (requests
            .GroupBy(
                request => CanonicalObjectId(request.ResourceDeploymentId),
                StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            return Invalid(
                "Requests",
                "A generated-binding set may contain each resource deployment only once.");
        }

        return null;
    }

    private static ConflictFailure ConcurrentGeneratedZoneConflict() =>
        Failures.Conflict(
            "A generated domain zone changed concurrently; retry the logical transaction.");

    private static ConflictFailure GeneratedHostnameConflict() =>
        Failures.Conflict(
            "A generated hostname or deployment binding is already reserved.");

    private static string CanonicalObjectId(string value) =>
        ObjectId.Parse(value).ToString();

    private static ValidationFailure Invalid(string field, string message) =>
        Failures.ValidationFailure(new Dictionary<string, string[]> { [field] = [message] });

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Domain mutations must run inside the caller-owned MongoDB transaction.");
    }
}
