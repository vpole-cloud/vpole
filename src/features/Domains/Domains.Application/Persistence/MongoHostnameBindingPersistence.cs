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
        DomainTransactions.EnsureActive(session);
        var bindings = await _bindings.Find(session, DomainFilters.BindingForDeployment(
                tenantId, resourceType, resourceId, resourceDeploymentId))
            .SortBy(item => item.Hostname).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return bindings.Select(DomainContractMappings.ToSnapshot).ToArray();
    }

    public async Task<Result<HostnameBindingSnapshot, GeneratedHostnameBindingError>>
        CreateGeneratedOutcomeAsync(
            CreateGeneratedHostnameBindingRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        var result = await CreateGeneratedSetOutcomeAsync(
            [request],
            session,
            cancellationToken);
        return result.Map(bindings => bindings[0]);
    }

    public async Task<Result<
        IReadOnlyList<HostnameBindingSnapshot>,
        GeneratedHostnameBindingError>> CreateGeneratedSetOutcomeAsync(
        IReadOnlyList<CreateGeneratedHostnameBindingRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        EnsureGeneratedBindingSet(requests);

        var tenantId = requests[0].TenantId;
        var zoneIds = requests
            .Select(request => request.TenantDomainZoneId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var zones = await _zones.Find(
                session,
                DomainFilters.ZonesById(tenantId, zoneIds))
            .ToListAsync(cancellationToken);
        var zonesById = zones.ToDictionary(
            zone => CanonicalObjectId(zone.Id),
            StringComparer.Ordinal);
        if (zoneIds.Any(zoneId => !zonesById.ContainsKey(CanonicalObjectId(zoneId))))
        {
            var missingZoneId = zoneIds.First(zoneId =>
                !zonesById.ContainsKey(CanonicalObjectId(zoneId)));
            return new Err<GeneratedHostnameBindingError>(
                new GeneratedHostnameBindingError(
                    new GeneratedHostnameBindingZoneNotFound(missingZoneId)));
        }

        var bindings = new List<HostnameBinding>(requests.Count);
        foreach (var request in requests)
        {
            var zone = zonesById[CanonicalObjectId(request.TenantDomainZoneId)];
            if (!string.Equals(
                    zone.DeploymentTargetId,
                    request.DeploymentTargetId,
                    StringComparison.Ordinal))
            {
                return new Err<GeneratedHostnameBindingError>(
                    new GeneratedHostnameBindingError(
                        new GeneratedHostnameBindingTargetMismatch(
                            zone.Id,
                            request.DeploymentTargetId)));
            }

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

        if (bindings
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
        var hostnames = bindings.Select(binding => binding.Hostname).ToArray();
        var existingConflict = await _bindings.Find(
                session,
                DomainFilters.GeneratedBindingConflicts(
                    tenantId,
                    hostnames,
                    deploymentIds))
            .Limit(1)
            .AnyAsync(cancellationToken);
        if (existingConflict)
        {
            return GeneratedBindingConflict(bindings);
        }

        if (!await TryAdvanceZoneRevisionsAsync(
            zones,
            session,
            cancellationToken))
        {
            return new Err<GeneratedHostnameBindingError>(
                new GeneratedHostnameBindingError(
                    new GeneratedHostnameBindingZoneChanged(
                        zones.Select(zone => zone.Id).ToArray())));
        }

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
            return GeneratedBindingConflict(bindings);
        }
        catch (MongoBulkWriteException<HostnameBinding> exception) when (
            exception.WriteErrors.Any(error =>
                error.Category == ServerErrorCategory.DuplicateKey))
        {
            return GeneratedBindingConflict(bindings);
        }

        return new Ok<IReadOnlyList<HostnameBindingSnapshot>>(
            bindings.Select(DomainContractMappings.ToSnapshot).ToArray());
    }

    private static Err<GeneratedHostnameBindingError> GeneratedBindingConflict(
        IReadOnlyList<HostnameBinding> bindings) =>
        new(new GeneratedHostnameBindingError(
            new GeneratedHostnameBindingConflict(
                bindings.Select(binding => binding.Hostname).ToArray())));

    public async Task<Result<HostnameBindingSnapshot, CustomHostnameBindingError>>
        CreateCustomOutcomeAsync(
            CreateCustomHostnameBindingRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        EnsureCustomCreateRequest(request);
        DomainTransactions.EnsureActive(session);
        var zone = await _zones.Find(
                session,
                DomainFilters.ZoneById(
                    request.TenantId,
                    request.TenantDomainZoneId))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
        {
            return new Err<CustomHostnameBindingError>(
                new CustomHostnameBindingError(
                    new CustomHostnameBindingZoneNotFound(request.TenantDomainZoneId)));
        }
        if (!string.Equals(
                zone.DeploymentTargetId,
                request.DeploymentTargetId,
                StringComparison.Ordinal))
        {
            return new Err<CustomHostnameBindingError>(
                new CustomHostnameBindingError(
                    new CustomHostnameBindingTargetMismatch(
                        zone.Id,
                        request.DeploymentTargetId)));
        }

        var binding = HostnameBindingFactory.CreateCustom(
            request.TenantId,
            zone.Id,
            request.DeploymentTargetId,
            request.ResourceType,
            request.ResourceId,
            request.ResourceDeploymentId,
            request.RelativeName,
            DnsName.Create(zone.Domain),
            BindingCreateOptions());

        if (!await TryAdvanceZoneRevisionsAsync(
            [zone],
            session,
            cancellationToken))
        {
            return new Err<CustomHostnameBindingError>(
                new CustomHostnameBindingError(
                    new CustomHostnameBindingZoneChanged([zone.Id])));
        }

        try
        {
            await _bindings.InsertOneAsync(session, binding, cancellationToken: cancellationToken);
            return new Ok<HostnameBindingSnapshot>(binding.ToSnapshot());
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new Err<CustomHostnameBindingError>(
                new CustomHostnameBindingError(
                    new CustomHostnameBindingConflict(binding.Hostname)));
        }
    }

    public async Task<Result<Unit, CustomHostnameBindingError>> DeleteCustomOutcomeAsync(
        DeleteCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureCustomDeleteRequest(request);
        DomainTransactions.EnsureActive(session);
        var filter = DomainFilters.BindingById(request);
        var binding = await _bindings.Find(session, filter).FirstOrDefaultAsync(cancellationToken);
        if (binding is null)
        {
            return new Err<CustomHostnameBindingError>(
                new CustomHostnameBindingError(
                    new CustomHostnameBindingNotFound(request.BindingId)));
        }
        if (binding.Kind == HostnameBindingKind.Generated)
        {
            return new Err<CustomHostnameBindingError>(
                new CustomHostnameBindingError(
                    new GeneratedHostnameBindingDeletionRejected(binding.Id)));
        }

        var zone = await _zones.Find(
                session,
                DomainFilters.ZoneById(
                    request.TenantId,
                    binding.TenantDomainZoneId))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
        {
            return new Err<CustomHostnameBindingError>(
                new CustomHostnameBindingError(
                    new CustomHostnameBindingZoneUnavailable(
                        binding.TenantDomainZoneId)));
        }
        if (!await TryAdvanceZoneRevisionsAsync(
            [zone],
            session,
            cancellationToken))
        {
            return new Err<CustomHostnameBindingError>(
                new CustomHostnameBindingError(
                    new CustomHostnameBindingZoneChanged([zone.Id])));
        }

        var deleted = await _bindings.DeleteOneAsync(session, filter, cancellationToken: cancellationToken);
        if (deleted.DeletedCount != 1)
        {
            return new Err<CustomHostnameBindingError>(
                new CustomHostnameBindingError(
                    new CustomHostnameBindingDeletionConflict(binding.Id)));
        }

        var released = await ReleaseGeneratedZonesWithoutBindingsAsync(
            [zone with { Revision = checked(zone.Revision + 1) }],
            session,
            cancellationToken);
        return released.MapError(_ => new CustomHostnameBindingError(
            new CustomHostnameBindingZoneChanged([zone.Id])));
    }

    public async Task<Result<Unit, HostnameBindingCleanupError>>
        DeleteAllForDeploymentAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
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

    public async Task<Result<Unit, HostnameBindingCleanupError>>
        DeleteAllForResourceAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        return await DeleteBindingsAndReleaseGeneratedZonesAsync(
            tenantId,
            DomainFilters.BindingsForResource(
                tenantId,
                resourceType,
                resourceId),
            session,
            cancellationToken);
    }

}
