using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application;

internal static class ComputeProfileFilters
{
    public static FilterDefinition<ComputeProfile> ByTechnicalName(string name) =>
        Builders<ComputeProfile>.Filter.Eq(item => item.TechnicalName, name);

    public static FilterDefinition<ComputeProfile> NamedForWorkload(string name, string tenantId, string targetId,
        ResourceTypeId resourceType) =>
        ByTechnicalName(name) & AllowsTenant(tenantId) & AvailableForTarget(targetId) &
        Builders<ComputeProfile>.Filter.AnyEq(profile => profile.AllowedResourceTypes, resourceType.Value);

    public static FilterDefinition<ComputeProfile> All() =>
        Builders<ComputeProfile>.Filter.Empty;

    public static FilterDefinition<ComputeProfile> ById(string computeProfileId) =>
        Builders<ComputeProfile>.Filter.Eq(profile => profile.Id, computeProfileId);

    public static FilterDefinition<ComputeProfile> ByIds(
        IEnumerable<string> computeProfileIds) =>
        Builders<ComputeProfile>.Filter.In(profile => profile.Id, computeProfileIds);

    public static FilterDefinition<ComputeProfile> AtRevision(
        string computeProfileId,
        long revision) =>
        Builders<ComputeProfile>.Filter.And(
            ById(computeProfileId),
            Builders<ComputeProfile>.Filter.Eq(profile => profile.Revision, revision));

    public static FilterDefinition<ComputeProfile> AtSnapshot(ComputeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Builders<ComputeProfile>.Filter.And(
            AtRevision(profile.Id, profile.Revision),
            Builders<ComputeProfile>.Filter.Eq(
                candidate => candidate.ResolutionFence,
                profile.ResolutionFence));
    }

    public static FilterDefinition<ComputeProfile> Default() =>
        Builders<ComputeProfile>.Filter.Eq(profile => profile.IsDefault, true);

    public static FilterDefinition<ComputeProfile> OtherDefaultsExcept(
        string computeProfileId) =>
        Builders<ComputeProfile>.Filter.And(
            Default(),
            Builders<ComputeProfile>.Filter.Ne(profile => profile.Id, computeProfileId));

    public static FilterDefinition<ComputeProfile> ByRequestedIdsOrDefault(
        IReadOnlyCollection<string> computeProfileIds) =>
        Builders<ComputeProfile>.Filter.Or(
            ByIds(computeProfileIds),
            Default());

    public static FilterDefinition<ComputeProfile> AvailableForTarget(
        string deploymentTargetId) =>
        Builders<ComputeProfile>.Filter.And(
            Builders<ComputeProfile>.Filter.Eq(profile => profile.IsEnabled, true),
            Builders<ComputeProfile>.Filter.ElemMatch(
                profile => profile.TargetMappings,
                mapping => mapping.DeploymentTargetId == deploymentTargetId));

    public static FilterDefinition<ComputeProfile> AllowsTenant(string tenantId) =>
        Builders<ComputeProfile>.Filter.Or(
            Builders<ComputeProfile>.Filter
                .OfType<ComputeProfileTenantAccess, AllTenantsComputeProfileAccess>(
                    profile => profile.TenantAccess),
            Builders<ComputeProfile>.Filter.And(
                Builders<ComputeProfile>.Filter
                    .OfType<ComputeProfileTenantAccess, SelectedTenantsComputeProfileAccess>(
                        profile => profile.TenantAccess),
                Builders<ComputeProfile>.Filter.AnyEq(
                    profile =>
                        ((SelectedTenantsComputeProfileAccess)profile.TenantAccess).TenantIds,
                    tenantId)));

    public static FilterDefinition<ComputeProfile> AvailableAtSnapshot(
        ComputeProfile profile,
        string tenantId,
        ResourceTypeId resourceType) =>
        Builders<ComputeProfile>.Filter.And(
            AtSnapshot(profile),
            Builders<ComputeProfile>.Filter.Eq(candidate => candidate.IsEnabled, true),
            Builders<ComputeProfile>.Filter.AnyEq(
                candidate => candidate.AllowedResourceTypes,
                resourceType.Value),
            AllowsTenant(tenantId),
            Builders<ComputeProfile>.Filter.Lt(
                candidate => candidate.ResolutionFence,
                long.MaxValue));
}

internal static class ComputeProfileRevisionFilters
{
    public static FilterDefinition<ComputeProfileRevision> Identity(string id, long revision) =>
        Builders<ComputeProfileRevision>.Filter.Eq(item => item.Id, $"{id}/{revision}");
}
