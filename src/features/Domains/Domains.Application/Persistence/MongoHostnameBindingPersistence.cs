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
        EnsureTransaction(session);
        var bindings = await _bindings.Find(session, DomainFilters.BindingForDeployment(
                tenantId, resourceType, resourceId, resourceDeploymentId))
            .SortBy(item => item.Hostname).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return bindings.Select(DomainContractMappings.ToSnapshot).ToArray();
    }

    public async Task<Result<HostnameBindingSnapshot>> CreateGeneratedAsync(
        CreateGeneratedHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateGeneratedOutcomeAsync(
            request,
            session,
            cancellationToken);
        return result switch
        {
            Ok<HostnameBindingSnapshot> success =>
                new Success<HostnameBindingSnapshot>(success.Value),
            Err<GeneratedHostnameBindingError> error =>
                ToLegacyFailure(error.Value)
        };
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

    public async Task<Result<IReadOnlyList<HostnameBindingSnapshot>>> CreateGeneratedSetAsync(
        IReadOnlyList<CreateGeneratedHostnameBindingRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateGeneratedSetOutcomeAsync(
            requests,
            session,
            cancellationToken);
        return result switch
        {
            Ok<IReadOnlyList<HostnameBindingSnapshot>> success =>
                new Success<IReadOnlyList<HostnameBindingSnapshot>>(success.Value),
            Err<GeneratedHostnameBindingError> error =>
                ToLegacyFailure(error.Value)
        };
    }

    private async Task<Result<
        IReadOnlyList<HostnameBindingSnapshot>,
        GeneratedHostnameBindingError>> CreateGeneratedSetOutcomeAsync(
        IReadOnlyList<CreateGeneratedHostnameBindingRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var validation = ValidateGeneratedBindingSet(requests);
        if (validation is not null)
        {
            return new Err<GeneratedHostnameBindingError>(
                new GeneratedHostnameBindingError(validation));
        }

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
                return InvalidGeneratedBinding("Hostname", exception.Message);
            }
        }

        if (bindings
            .GroupBy(binding => binding.Hostname, StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            return InvalidGeneratedBinding(
                "Requests",
                "Generated hostnames must be unique within a binding set.");
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

        var advancedZones = await AdvanceZoneRevisionsAsync(
            zones,
            session,
            cancellationToken);
        if (advancedZones is not ISuccess)
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

    private static Err<GeneratedHostnameBindingError> InvalidGeneratedBinding(
        string field,
        string message) =>
        new(new GeneratedHostnameBindingError(
            new InvalidGeneratedHostnameBindingRequest(
                new Dictionary<string, string[]> { [field] = [message] })));

    private static Err<GeneratedHostnameBindingError> GeneratedBindingConflict(
        IReadOnlyList<HostnameBinding> bindings) =>
        new(new GeneratedHostnameBindingError(
            new GeneratedHostnameBindingConflict(
                bindings.Select(binding => binding.Hostname).ToArray())));

    public async Task<Result<HostnameBindingSnapshot>> CreateCustomAsync(
        CreateCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var zone = await _zones.Find(
                session,
                DomainFilters.ZoneById(
                    request.TenantId,
                    request.TenantDomainZoneId))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
            return Failures.NotFound("Domain zone not found.", request.TenantDomainZoneId);
        if (!string.Equals(
                zone.DeploymentTargetId,
                request.DeploymentTargetId,
                StringComparison.Ordinal))
        {
            return Failures.Conflict("The domain zone belongs to another deployment target.");
        }

        HostnameBinding binding;
        try
        {
            binding = HostnameBindingFactory.CreateCustom(
                request.TenantId,
                zone.Id,
                request.DeploymentTargetId,
                request.ResourceType,
                request.ResourceId,
                request.ResourceDeploymentId,
                request.RelativeName,
                DnsName.Create(zone.Domain),
                BindingCreateOptions());
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
        var filter = DomainFilters.BindingById(request);
        var binding = await _bindings.Find(session, filter).FirstOrDefaultAsync(cancellationToken);
        if (binding is null)
            return Failures.NotFound("Hostname binding not found.", request.BindingId);
        if (binding.Kind == HostnameBindingKind.Generated)
            return Failures.Conflict("Generated hostname bindings cannot be deleted independently.");

        var zone = await _zones.Find(
                session,
                DomainFilters.ZoneById(
                    request.TenantId,
                    binding.TenantDomainZoneId))
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
            DomainFilters.BindingsForResource(
                tenantId,
                resourceType,
                resourceId),
            session,
            cancellationToken);
    }

}
