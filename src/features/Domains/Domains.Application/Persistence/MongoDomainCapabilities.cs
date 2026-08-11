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

/// <summary>
/// Capability-oriented persistence for zones and bindings. It intentionally owns its collections
/// directly instead of exporting a broad database-context interface to other features.
/// </summary>
public sealed class MongoDomainCapabilities(
    IMongoDatabase database,
    IDeploymentTargetReferenceAdmission targetReferenceAdmission,
    IDeploymentTargetWorkloadConfigurationFence targetWorkloadConfigurationFence,
    ITenantIdentityLookup tenantIdentityLookup,
    TimeProvider timeProvider)
    : ITenantDomainZoneCapability, IHostnameBindingCapability
{
    private readonly IMongoCollection<TenantDomainZone> _zones =
        database.GetCollection<TenantDomainZone>("TenantDomainZones");
    private readonly IMongoCollection<HostnameBinding> _bindings =
        database.GetCollection<HostnameBinding>("HostnameBindings");

    public async Task<Result<TenantDomainZoneSnapshot>> GetAsync(
        string tenantId,
        string zoneId,
        CancellationToken cancellationToken = default)
    {
        var zone = await _zones.Find(Builders<TenantDomainZone>.Filter.And(
                DomainFilters.ZoneForTenant(tenantId),
                Builders<TenantDomainZone>.Filter.Eq(item => item.Id, zoneId)))
            .FirstOrDefaultAsync(cancellationToken);
        return zone is null
            ? Failures.NotFound("Domain zone not found.", zoneId)
            : zone.ToSnapshot();
    }

    public async Task<IReadOnlyList<TenantDomainZoneSnapshot>> ListAsync(
        string tenantId,
        string? deploymentTargetId = null,
        CancellationToken cancellationToken = default)
    {
        var filter = DomainFilters.ZoneForTenant(tenantId);
        if (!string.IsNullOrWhiteSpace(deploymentTargetId))
            filter &= Builders<TenantDomainZone>.Filter.Eq(item => item.DeploymentTargetId, deploymentTargetId);
        var zones = await _zones.Find(filter)
            .SortBy(item => item.DeploymentTargetId).ThenBy(item => item.Domain).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return zones.Select(DomainContractMappings.ToSnapshot).ToArray();
    }

    public async Task<Result<TenantDomainZoneSnapshot>> GetOrCreateGeneratedAsync(
        GetOrCreateGeneratedZoneRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var result = await GetOrCreateGeneratedSetAsync(
            [request],
            session,
            cancellationToken);
        return result.TryGetValue(out var zones, out var failure)
            ? zones[0]
            : failure;
    }

    public async Task<Result<IReadOnlyList<TenantDomainZoneSnapshot>>> GetOrCreateGeneratedSetAsync(
        IReadOnlyList<GetOrCreateGeneratedZoneRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var validation = ValidateGeneratedZoneSet(requests);
        if (validation is not null)
            return validation;

        var tenantId = requests[0].TenantId;
        var identity = await tenantIdentityLookup.GetAsync(tenantId, cancellationToken);
        if (!identity.TryGetValue(out var tenant, out var identityFailure))
            return identityFailure;

        var candidates = new List<TenantDomainZone>(requests.Count);
        foreach (var request in requests)
        {
            if (!TryCreateGeneratedZone(
                    request,
                    tenant.DisplayName,
                    out var candidate,
                    out var failure))
            {
                return failure!;
            }
            candidates.Add(candidate!);
        }

        var targetIds = requests.Select(request => request.DeploymentTargetId).ToArray();
        var existing = await _zones.Find(
                session,
                Builders<TenantDomainZone>.Filter.And(
                    DomainFilters.ZoneForTenant(tenantId),
                    Builders<TenantDomainZone>.Filter.In(
                        zone => zone.DeploymentTargetId,
                        targetIds),
                    Builders<TenantDomainZone>.Filter.Eq(
                        zone => zone.Kind,
                        TenantDomainZoneKind.Generated)))
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
                return Failures.Conflict(
                    "The generated domain zone does not match the target public base domain.");
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
            var fence = await targetWorkloadConfigurationFence.FenceManyAsync(
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
            if (fence is not ISuccess)
                return (Failure)fence;

            try
            {
                await _zones.InsertManyAsync(
                    session,
                    missing,
                    new InsertManyOptions { IsOrdered = true },
                    cancellationToken);
            }
            catch (MongoWriteException exception) when (
                exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                return ConcurrentGeneratedZoneConflict();
            }
            catch (MongoBulkWriteException<TenantDomainZone> exception) when (
                exception.WriteErrors.Any(error =>
                    error.Category == ServerErrorCategory.DuplicateKey))
            {
                return ConcurrentGeneratedZoneConflict();
            }

            foreach (var candidate in missing)
            {
                var registered = await targetReferenceAdmission.RegisterAsync(
                    candidate.DeploymentTargetId,
                    new DeploymentTargetReferenceOwner(
                        DeploymentTargetReferenceOwnerTypes.DomainZone,
                        candidate.Id),
                    session,
                    cancellationToken);
                if (registered is not ISuccess)
                    return (Failure)registered;
                existingByTarget.Add(
                    CanonicalObjectId(candidate.DeploymentTargetId),
                    candidate);
            }
        }

        return requests
            .Select(request => existingByTarget[
                CanonicalObjectId(request.DeploymentTargetId)].ToSnapshot())
            .ToArray();
    }

    public async Task<Result<TenantDomainZoneSnapshot>> CreateManagedReservationAsync(
        string tenantId,
        string deploymentTargetId,
        string label,
        string publicBaseDomain,
        long expectedWorkloadConfigurationRevision,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        TenantDomainZone zone;
        try
        {
            zone = TenantDomainZoneFactory.CreateManagedReservation(
                tenantId,
                deploymentTargetId,
                label,
                DnsName.Create(publicBaseDomain),
                CreateOptions());
        }
        catch (ArgumentException exception)
        {
            return Invalid("DomainZone", exception.Message);
        }

        var fence = await targetWorkloadConfigurationFence.FenceAsync(
            deploymentTargetId,
            expectedWorkloadConfigurationRevision,
            session,
            cancellationToken);
        if (fence is not ISuccess)
            return (Failure)fence;

        try
        {
            await _zones.InsertOneAsync(session, zone, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return Failures.Conflict("The requested domain zone is already reserved.");
        }

        var registered = await targetReferenceAdmission.RegisterAsync(
            zone.DeploymentTargetId,
            new DeploymentTargetReferenceOwner(DeploymentTargetReferenceOwnerTypes.DomainZone, zone.Id),
            session,
            cancellationToken);
        return registered is ISuccess ? zone.ToSnapshot() : (Failure)registered;
    }

    public async Task<Result> DeleteManagedReservationAsync(
        string tenantId,
        string zoneId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var zoneFilter = Builders<TenantDomainZone>.Filter.And(
            DomainFilters.ZoneForTenant(tenantId),
            Builders<TenantDomainZone>.Filter.Eq(item => item.Id, zoneId));
        var zone = await _zones.Find(session, zoneFilter).FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
            return Failures.NotFound("Domain zone not found.", zoneId);
        if (zone.Kind == TenantDomainZoneKind.Generated)
            return Failures.Conflict("Generated domain zones are managed by the platform.");
        var bindingCount = await _bindings.CountDocumentsAsync(
            session,
            Builders<HostnameBinding>.Filter.Eq(item => item.TenantDomainZoneId, zoneId),
            cancellationToken: cancellationToken);
        if (bindingCount > 0)
            return Failures.Conflict("A domain zone cannot be deleted while hostname bindings refer to it.");
        var deleted = await _zones.DeleteOneAsync(
            session,
            Builders<TenantDomainZone>.Filter.And(
                zoneFilter,
                Builders<TenantDomainZone>.Filter.Eq(item => item.Revision, zone.Revision)),
            cancellationToken: cancellationToken);
        if (deleted.DeletedCount != 1)
            return Failures.Conflict("The domain zone changed while deletion was being accepted.");
        return await targetReferenceAdmission.UnregisterAsync(
            zone.DeploymentTargetId,
            new DeploymentTargetReferenceOwner(DeploymentTargetReferenceOwnerTypes.DomainZone, zone.Id),
            session,
            cancellationToken);
    }

    public async Task<IReadOnlyList<HostnameBindingSnapshot>> ListForDeploymentAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        CancellationToken cancellationToken = default)
    {
        var bindings = await _bindings.Find(DomainFilters.BindingForDeployment(
                tenantId, resourceType, resourceId, resourceDeploymentId))
            .SortBy(item => item.Hostname).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return bindings.Select(DomainContractMappings.ToSnapshot).ToArray();
    }

    public async Task<IReadOnlyList<HostnameBindingSnapshot>> ListForDeploymentInTransactionAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var bindings = await _bindings.Find(session, DomainFilters.BindingForDeployment(
                tenantId, resourceType, resourceId, resourceDeploymentId))
            .SortBy(item => item.Hostname).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return bindings.Select(DomainContractMappings.ToSnapshot).ToArray();
    }

    public async Task<IReadOnlyList<string>> ListHostnamesAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        CancellationToken cancellationToken = default) =>
        (await ListForDeploymentAsync(
            tenantId, resourceType, resourceId, resourceDeploymentId, cancellationToken))
        .Select(item => item.Hostname)
        .Order(StringComparer.Ordinal)
        .ToArray();

    public async Task<Result<HostnameBindingSnapshot>> CreateGeneratedAsync(
        CreateGeneratedHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateGeneratedSetAsync(
            [request],
            session,
            cancellationToken);
        return result.TryGetValue(out var bindings, out var failure)
            ? bindings[0]
            : failure;
    }

    public async Task<Result<IReadOnlyList<HostnameBindingSnapshot>>> CreateGeneratedSetAsync(
        IReadOnlyList<CreateGeneratedHostnameBindingRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var validation = ValidateGeneratedBindingSet(requests);
        if (validation is not null)
            return validation;

        var tenantId = requests[0].TenantId;
        var zoneIds = requests
            .Select(request => request.TenantDomainZoneId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var zones = await _zones.Find(
                session,
                Builders<TenantDomainZone>.Filter.And(
                    DomainFilters.ZoneForTenant(tenantId),
                    Builders<TenantDomainZone>.Filter.In(zone => zone.Id, zoneIds)))
            .ToListAsync(cancellationToken);
        var zonesById = zones.ToDictionary(
            zone => CanonicalObjectId(zone.Id),
            StringComparer.Ordinal);
        if (zoneIds.Any(zoneId => !zonesById.ContainsKey(CanonicalObjectId(zoneId))))
            return Failures.NotFound("One or more domain zones were not found.");

        var bindings = new List<HostnameBinding>(requests.Count);
        foreach (var request in requests)
        {
            var zone = zonesById[CanonicalObjectId(request.TenantDomainZoneId)];
            if (!string.Equals(
                    zone.DeploymentTargetId,
                    request.DeploymentTargetId,
                    StringComparison.Ordinal))
            {
                return Failures.Conflict(
                    "A domain zone belongs to another deployment target.");
            }

            try
            {
                bindings.Add(HostnameBindingFactory.CreateGenerated(
                    request.TenantId,
                    zone.Id,
                    request.DeploymentTargetId,
                    request.ResourceType,
                    request.ResourceId,
                    request.ResourceDeploymentId,
                    request.RelativeName,
                    DnsName.Create(zone.Domain),
                    BindingCreateOptions()));
            }
            catch (ArgumentException exception)
            {
                return Invalid("Hostname", exception.Message);
            }
        }

        if (bindings
            .GroupBy(binding => binding.Hostname, StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            return Invalid(
                "Requests",
                "Generated hostnames must be unique within a binding set.");
        }

        var deploymentIds = requests
            .Select(request => request.ResourceDeploymentId)
            .ToArray();
        var hostnames = bindings.Select(binding => binding.Hostname).ToArray();
        var existingConflict = await _bindings.Find(
                session,
                Builders<HostnameBinding>.Filter.And(
                    Builders<HostnameBinding>.Filter.ForTenant(tenantId),
                    Builders<HostnameBinding>.Filter.Or(
                        Builders<HostnameBinding>.Filter.In(
                            binding => binding.Hostname,
                            hostnames),
                        Builders<HostnameBinding>.Filter.And(
                            Builders<HostnameBinding>.Filter.In(
                                binding => binding.ResourceDeploymentId,
                                deploymentIds),
                            Builders<HostnameBinding>.Filter.Eq(
                                binding => binding.Kind,
                                HostnameBindingKind.Generated)))))
            .Limit(1)
            .AnyAsync(cancellationToken);
        if (existingConflict)
            return Failures.Conflict(
                "A generated hostname or deployment binding is already reserved.");

        var advancedZones = await AdvanceZoneRevisionsAsync(
            zones,
            session,
            cancellationToken);
        if (advancedZones is not ISuccess)
            return (Failure)advancedZones;

        try
        {
            await _bindings.InsertManyAsync(
                session,
                bindings,
                new InsertManyOptions { IsOrdered = true },
                cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return GeneratedHostnameConflict();
        }
        catch (MongoBulkWriteException<HostnameBinding> exception) when (
            exception.WriteErrors.Any(error =>
                error.Category == ServerErrorCategory.DuplicateKey))
        {
            return GeneratedHostnameConflict();
        }

        return bindings.Select(DomainContractMappings.ToSnapshot).ToArray();
    }

    public Task<Result<HostnameBindingSnapshot>> CreateCustomAsync(
        CreateCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        CreateBindingAsync(
            request.TenantId,
            request.TenantDomainZoneId,
            request.DeploymentTargetId,
            request.ResourceType,
            request.ResourceId,
            request.ResourceDeploymentId,
            request.RelativeName,
            HostnameBindingKind.Custom,
            session,
            cancellationToken);

    private async Task<Result<HostnameBindingSnapshot>> CreateBindingAsync(
        string tenantId,
        string tenantDomainZoneId,
        string deploymentTargetId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        string relativeName,
        HostnameBindingKind kind,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var zone = await _zones.Find(session, Builders<TenantDomainZone>.Filter.And(
                DomainFilters.ZoneForTenant(tenantId),
                Builders<TenantDomainZone>.Filter.Eq(item => item.Id, tenantDomainZoneId)))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
            return Failures.NotFound("Domain zone not found.", tenantDomainZoneId);
        if (!string.Equals(zone.DeploymentTargetId, deploymentTargetId, StringComparison.Ordinal))
            return Failures.Conflict("The domain zone belongs to another deployment target.");

        HostnameBinding binding;
        try
        {
            binding = kind == HostnameBindingKind.Generated
                ? HostnameBindingFactory.CreateGenerated(
                    tenantId, zone.Id, deploymentTargetId, resourceType,
                    resourceId, resourceDeploymentId, relativeName,
                    DnsName.Create(zone.Domain), BindingCreateOptions())
                : HostnameBindingFactory.CreateCustom(
                    tenantId, zone.Id, deploymentTargetId, resourceType,
                    resourceId, resourceDeploymentId, relativeName,
                    DnsName.Create(zone.Domain), BindingCreateOptions());
        }
        catch (ArgumentException exception)
        {
            return Invalid("Hostname", exception.Message);
        }

        var advancedZone = await AdvanceZoneRevisionsAsync(
            [zone],
            session,
            cancellationToken);
        if (advancedZone is not ISuccess)
            return (Failure)advancedZone;

        try
        {
            await _bindings.InsertOneAsync(session, binding, cancellationToken: cancellationToken);
            return binding.ToSnapshot();
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return Failures.Conflict("The hostname is already bound to a deployment.");
        }
    }

    public async Task<Result> DeleteCustomAsync(
        DeleteCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var filter = Builders<HostnameBinding>.Filter.And(
            DomainFilters.BindingForDeployment(
                request.TenantId,
                request.ResourceType,
                request.ResourceId,
                request.ResourceDeploymentId),
            Builders<HostnameBinding>.Filter.Eq(item => item.Id, request.BindingId));
        var binding = await _bindings.Find(session, filter).FirstOrDefaultAsync(cancellationToken);
        if (binding is null)
            return Failures.NotFound("Hostname binding not found.", request.BindingId);
        if (binding.Kind == HostnameBindingKind.Generated)
            return Failures.Conflict("Generated hostname bindings cannot be deleted independently.");

        var zone = await _zones.Find(
                session,
                Builders<TenantDomainZone>.Filter.And(
                    DomainFilters.ZoneForTenant(request.TenantId),
                    Builders<TenantDomainZone>.Filter.Eq(
                        item => item.Id,
                        binding.TenantDomainZoneId)))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
            return Failures.Conflict("The hostname binding's domain zone is unavailable.");
        var advancedZone = await AdvanceZoneRevisionsAsync(
            [zone],
            session,
            cancellationToken);
        if (advancedZone is not ISuccess)
            return (Failure)advancedZone;

        var deleted = await _bindings.DeleteOneAsync(session, filter, cancellationToken: cancellationToken);
        if (deleted.DeletedCount != 1)
            return Failures.Conflict("The hostname binding changed while deletion was being accepted.");
        return await ReleaseGeneratedZonesWithoutBindingsAsync(
            [zone with { Revision = checked(zone.Revision + 1) }],
            session,
            cancellationToken);
    }

    public async Task<Result> DeleteAllForDeploymentAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        return await DeleteBindingsAndReleaseGeneratedZonesAsync(
            tenantId,
            DomainFilters.BindingForDeployment(
                tenantId,
                resourceType,
                resourceId,
                resourceDeploymentId),
            session,
            cancellationToken);
    }

    public async Task<Result> DeleteAllForResourceAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        return await DeleteBindingsAndReleaseGeneratedZonesAsync(
            tenantId,
            Builders<HostnameBinding>.Filter.And(
                Builders<HostnameBinding>.Filter.Eq(item => item.TenantId, tenantId),
                Builders<HostnameBinding>.Filter.Eq(item => item.ResourceType, resourceType.Value),
                Builders<HostnameBinding>.Filter.Eq(item => item.ResourceId, resourceId)),
            session,
            cancellationToken);
    }

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
                Builders<TenantDomainZone>.Filter.And(
                    DomainFilters.ZoneForTenant(tenantId),
                    Builders<TenantDomainZone>.Filter.In(zone => zone.Id, zoneIds)))
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

        var expectedZones = distinctZones.Select(zone =>
            Builders<TenantDomainZone>.Filter.And(
                Builders<TenantDomainZone>.Filter.Eq(item => item.Id, zone.Id),
                Builders<TenantDomainZone>.Filter.Eq(item => item.Revision, zone.Revision)));
        var update = await _zones.UpdateManyAsync(
            session,
            Builders<TenantDomainZone>.Filter.Or(expectedZones),
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
                Builders<HostnameBinding>.Filter.In(
                    binding => binding.TenantDomainZoneId,
                    generatedZoneIds))
            .Project(binding => binding.TenantDomainZoneId)
            .ToListAsync(cancellationToken);
        var referenced = referencedZoneIds.ToHashSet(StringComparer.Ordinal);
        var releasable = generatedZones
            .Where(zone => !referenced.Contains(zone.Id))
            .ToArray();
        if (releasable.Length == 0)
            return Result.Success();

        var exactZones = releasable.Select(zone =>
            Builders<TenantDomainZone>.Filter.And(
                Builders<TenantDomainZone>.Filter.Eq(item => item.Id, zone.Id),
                Builders<TenantDomainZone>.Filter.Eq(item => item.Revision, zone.Revision),
                Builders<TenantDomainZone>.Filter.Eq(
                    item => item.Kind,
                    TenantDomainZoneKind.Generated)));
        var deletion = await _zones.DeleteManyAsync(
            session,
            Builders<TenantDomainZone>.Filter.Or(exactZones),
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
        const int suffixLength = 9; // hyphen plus eight ObjectId characters
        var maximumSlugLength = DnsLabel.MaximumLength -
            TenantDomainZoneLabelPolicy.GeneratedPrefix.Length -
            suffixLength;
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
