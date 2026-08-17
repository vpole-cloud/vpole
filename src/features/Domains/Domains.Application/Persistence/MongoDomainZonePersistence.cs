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

internal sealed partial class MongoDomainPersistence
{
    public async Task<Result<TenantDomainZoneSnapshot, DomainZoneNotFound>> GetAsync(
        string tenantId,
        string zoneId,
        CancellationToken cancellationToken = default)
    {
        var zone = await _zones.Find(DomainFilters.ZoneById(tenantId, zoneId))
            .FirstOrDefaultAsync(cancellationToken);
        return zone is null
            ? new Err<DomainZoneNotFound>(new DomainZoneNotFound(zoneId))
            : new Ok<TenantDomainZoneSnapshot>(zone.ToSnapshot());
    }

    public async Task<IReadOnlyList<TenantDomainZoneSnapshot>> ListAsync(
        string tenantId,
        string? deploymentTargetId = null,
        CancellationToken cancellationToken = default)
    {
        var zones = await _zones.Find(
                DomainFilters.ZonesForTenant(tenantId, deploymentTargetId))
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

    public async Task<Result<TenantDomainZoneSnapshot, CreateManagedDomainZoneError>>
        CreateManagedReservationOutcomeAsync(
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
            return new Err<CreateManagedDomainZoneError>(
                new InvalidManagedDomainZone(exception.Message));
        }

        var fence = await targetWorkloadConfigurationFence.FenceAsync(
            deploymentTargetId,
            expectedWorkloadConfigurationRevision,
            session,
            cancellationToken);
        if (fence is not ISuccess)
        {
            return new Err<CreateManagedDomainZoneError>(
                new ManagedDomainZoneDeploymentTargetChanged(deploymentTargetId));
        }

        try
        {
            await _zones.InsertOneAsync(session, zone, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new Err<CreateManagedDomainZoneError>(
                new ManagedDomainZoneAlreadyReserved(zone.Domain));
        }

        var registered = await targetReferenceAdmission.RegisterAsync(
            zone.DeploymentTargetId,
            new DeploymentTargetReferenceOwner(DeploymentTargetReferenceOwnerTypes.DomainZone, zone.Id),
            session,
            cancellationToken);
        return registered is ISuccess
            ? new Ok<TenantDomainZoneSnapshot>(
                zone.ToSnapshot())
            : new Err<CreateManagedDomainZoneError>(
                new ManagedDomainZoneReferenceRejected(deploymentTargetId));
    }

    public async Task<Result<ManagedDomainZoneDeleted, DeleteManagedDomainZoneError>>
        DeleteManagedReservationAsync(
            string tenantId,
            string zoneId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var zoneFilter = DomainFilters.ZoneById(tenantId, zoneId);
        var zone = await _zones.Find(session, zoneFilter).FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
        {
            return new Err<DeleteManagedDomainZoneError>(
                new DomainZoneNotFound(zoneId));
        }
        if (zone.Kind == TenantDomainZoneKind.Generated)
        {
            return new Err<DeleteManagedDomainZoneError>(
                new GeneratedDomainZoneDeletionRejected(zoneId));
        }
        var bindingCount = await _bindings.CountDocumentsAsync(
            session,
            DomainFilters.BindingsForZone(zoneId),
            cancellationToken: cancellationToken);
        if (bindingCount > 0)
        {
            return new Err<DeleteManagedDomainZoneError>(
                new DomainZoneHasHostnameBindings(zoneId, bindingCount));
        }
        var deleted = await _zones.DeleteOneAsync(
            session,
            DomainFilters.ZoneAtRevision(tenantId, zone),
            cancellationToken: cancellationToken);
        if (deleted.DeletedCount != 1)
        {
            return new Err<DeleteManagedDomainZoneError>(
                new DomainZoneDeletionConflict(zoneId));
        }

        var unregistered = await targetReferenceAdmission.UnregisterAsync(
            zone.DeploymentTargetId,
            new DeploymentTargetReferenceOwner(DeploymentTargetReferenceOwnerTypes.DomainZone, zone.Id),
            session,
            cancellationToken);
        return unregistered is ISuccess
            ? new Ok<ManagedDomainZoneDeleted>(
                new ManagedDomainZoneDeleted(zone.Id))
            : new Err<DeleteManagedDomainZoneError>(
                new ManagedDomainZoneReferenceRemovalRejected(
                    zone.Id,
                    zone.DeploymentTargetId));
    }

}
