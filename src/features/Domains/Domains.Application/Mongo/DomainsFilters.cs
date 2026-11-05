using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.HostnameBindings;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.Mongo;

internal static class DomainFilters
{
    public static FilterDefinition<TenantDomainZone> ZoneForTenant(string tenantId) =>
        Builders<TenantDomainZone>.Filter.ForTenant(tenantId);

    public static FilterDefinition<TenantDomainZone> ZoneById(
        string tenantId,
        string zoneId) =>
        Builders<TenantDomainZone>.Filter.And(
            ZoneForTenant(tenantId),
            Builders<TenantDomainZone>.Filter.Eq(zone => zone.Id, zoneId));

    public static FilterDefinition<TenantDomainZone> ZoneByTechnicalName(
        string tenantId,
        string technicalName) =>
        ZoneForTenant(tenantId) & Builders<TenantDomainZone>.Filter.OfType<DomainIdentity, NamedDomainIdentity>(
            item => item.Identity) &
        Builders<TenantDomainZone>.Filter.Eq(
            item => ((NamedDomainIdentity)item.Identity).TechnicalName, technicalName);

    public static FilterDefinition<TenantDomainZone> ZoneAtPlanningVersion(FenceDomainZoneRequest request) =>
        ZoneById(request.TenantId, request.Id) &
        Builders<TenantDomainZone>.Filter.Eq(item => item.Revision, request.ExpectedRevision) &
        Builders<TenantDomainZone>.Filter.Eq(item => item.ReferenceFence, request.ExpectedReferenceFence);

    public static FilterDefinition<HostnameBinding> BindingByTechnicalName(
        string tenantId,
        string technicalName) =>
        Builders<HostnameBinding>.Filter.ForTenant(
            tenantId) & Builders<HostnameBinding>.Filter.OfType<DomainIdentity, NamedDomainIdentity>(item =>
            item.Identity) &
        Builders<HostnameBinding>.Filter.Eq(
            item => ((NamedDomainIdentity)item.Identity).TechnicalName, technicalName);

    public static FilterDefinition<HostnameBinding> BindingById(string tenantId, string id) =>
        Builders<HostnameBinding>.Filter.ForTenant(tenantId) &
        Builders<HostnameBinding>.Filter.Eq(item => item.Id, id);

    public static FilterDefinition<HostnameBinding> BindingAtPlanningVersion(FenceHostnameBindingRequest request) =>
        BindingById(request.TenantId, request.Id) &
        Builders<HostnameBinding>.Filter.Eq(item => item.ReferenceFence, request.ExpectedReferenceFence);

    public static FilterDefinition<TenantDomainZone> ZonesForTenant(
        string tenantId,
        string? deploymentTargetId) =>
        string.IsNullOrWhiteSpace(deploymentTargetId)
            ? ZoneForTenant(tenantId)
            : Builders<TenantDomainZone>.Filter.And(
                ZoneForTenant(tenantId),
                Builders<TenantDomainZone>.Filter.Eq(
                    zone => zone.DeploymentTargetId,
                    deploymentTargetId));

    public static FilterDefinition<TenantDomainZone> ZonesById(
        string tenantId,
        IReadOnlyCollection<string> zoneIds) =>
        Builders<TenantDomainZone>.Filter.And(
            ZoneForTenant(tenantId),
            Builders<TenantDomainZone>.Filter.In(zone => zone.Id, zoneIds));

    public static FilterDefinition<TenantDomainZone> GeneratedZonesForTargets(
        string tenantId,
        IReadOnlyCollection<string> deploymentTargetIds) =>
        Builders<TenantDomainZone>.Filter.And(
            ZoneForTenant(tenantId),
            Builders<TenantDomainZone>.Filter.In(
                zone => zone.DeploymentTargetId,
                deploymentTargetIds),
            Builders<TenantDomainZone>.Filter.OfType<DomainIdentity, GeneratedDomainIdentity>(item => item.Identity));

    public static FilterDefinition<TenantDomainZone> AtRevision(
        TenantDomainZone zone) =>
        Builders<TenantDomainZone>.Filter.And(
            Builders<TenantDomainZone>.Filter.Eq(item => item.Id, zone.Id),
            Builders<TenantDomainZone>.Filter.Eq(
                item => item.Revision,
                zone.Revision));

    public static FilterDefinition<TenantDomainZone> ZoneAtRevision(
        string tenantId,
        TenantDomainZone zone) =>
        Builders<TenantDomainZone>.Filter.And(
            ZoneById(tenantId, zone.Id),
            AtRevision(zone));

    public static FilterDefinition<TenantDomainZone> AtRevisions(
        IReadOnlyCollection<TenantDomainZone> zones) =>
        Builders<TenantDomainZone>.Filter.Or(zones.Select(AtRevision));

    public static FilterDefinition<TenantDomainZone> GeneratedAtRevisions(
        IReadOnlyCollection<TenantDomainZone> zones) =>
        Builders<TenantDomainZone>.Filter.Or(zones.Select(zone =>
            Builders<TenantDomainZone>.Filter.And(
                AtRevision(zone),
                Builders<TenantDomainZone>.Filter.OfType<DomainIdentity, GeneratedDomainIdentity>(item =>
                    item.Identity))));

    public static FilterDefinition<HostnameBinding> BindingForDeployment(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId) =>
        Builders<HostnameBinding>.Filter.And(
            Builders<HostnameBinding>.Filter.ForTenant(tenantId),
            Builders<HostnameBinding>.Filter.Eq(item => item.ResourceType, resourceType.Value),
            Builders<HostnameBinding>.Filter.Eq(item => item.ResourceId, resourceId),
            Builders<HostnameBinding>.Filter.Eq(item => item.ResourceDeploymentId, resourceDeploymentId));

    public static FilterDefinition<HostnameBinding> BindingById(
        DeleteCustomHostnameBindingRequest request) =>
        Builders<HostnameBinding>.Filter.And(
            BindingForDeployment(
                request.TenantId,
                request.ResourceType,
                request.ResourceId,
                request.ResourceDeploymentId),
            Builders<HostnameBinding>.Filter.Eq(
                binding => binding.Id,
                request.BindingId));

    public static FilterDefinition<HostnameBinding> BindingsForResource(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId) =>
        Builders<HostnameBinding>.Filter.And(
            Builders<HostnameBinding>.Filter.ForTenant(tenantId),
            Builders<HostnameBinding>.Filter.Eq(
                binding => binding.ResourceType,
                resourceType.Value),
            Builders<HostnameBinding>.Filter.Eq(
                binding => binding.ResourceId,
                resourceId));

    public static FilterDefinition<HostnameBinding> BindingsForZones(
        IReadOnlyCollection<string> zoneIds) =>
        Builders<HostnameBinding>.Filter.In(
            binding => binding.TenantDomainZoneId,
            zoneIds);

    public static FilterDefinition<HostnameBinding> BindingsForZone(string zoneId) =>
        Builders<HostnameBinding>.Filter.Eq(
            binding => binding.TenantDomainZoneId,
            zoneId);

    public static FilterDefinition<HostnameBinding> BindingsForTenantZone(string tenantId, string zoneId) =>
        Builders<HostnameBinding>.Filter.And(
            Builders<HostnameBinding>.Filter.ForTenant(tenantId),
            BindingsForZone(zoneId));

    public static FilterDefinition<HostnameBinding> GeneratedBindingConflicts(
        string tenantId,
        IReadOnlyCollection<string> hostnames,
        IReadOnlyCollection<string> deploymentIds) =>
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
                    Builders<HostnameBinding>.Filter.OfType<DomainIdentity, GeneratedDomainIdentity>(item =>
                        item.Identity))));
}
