using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal static class ComputeProfileFilters
{
    public static FilterDefinition<ComputeProfile> ById(string computeProfileId) =>
        Builders<ComputeProfile>.Filter.Eq(profile => profile.Id, computeProfileId);

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

    public static FilterDefinition<ComputeProfile> AvailableForResolutionFence(
        ComputeProfileResolutionFenceRequest request) =>
        Builders<ComputeProfile>.Filter.And(
            AtRevision(
                request.ComputeProfileId,
                request.ComputeProfileRevision),
            Builders<ComputeProfile>.Filter.Eq(profile => profile.IsEnabled, true),
            Builders<ComputeProfile>.Filter.AnyEq(
                profile => profile.AllowedResourceTypes,
                request.ResourceType.Value),
            Builders<ComputeProfile>.Filter.ElemMatch(
                profile => profile.TargetMappings,
                mapping => mapping.DeploymentTargetId == request.DeploymentTargetId),
            AllowsTenant(request.TenantId),
            Builders<ComputeProfile>.Filter.Lt(
                profile => profile.ResolutionFence,
                long.MaxValue));
}
