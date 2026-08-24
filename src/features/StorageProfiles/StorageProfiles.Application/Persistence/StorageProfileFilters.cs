using MongoDB.Driver;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Persistence;

internal static class StorageProfileFilters
{
    public static FilterDefinition<StorageProfile> All() =>
        Builders<StorageProfile>.Filter.Empty;

    public static FilterDefinition<StorageProfile> ById(string storageProfileId) =>
        Builders<StorageProfile>.Filter.Eq(profile => profile.Id, storageProfileId);

    public static FilterDefinition<StorageProfile> AtSnapshot(StorageProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Builders<StorageProfile>.Filter.And(
            ById(profile.Id),
            Builders<StorageProfile>.Filter.Eq(candidate => candidate.Revision, profile.Revision),
            Builders<StorageProfile>.Filter.Eq(
                candidate => candidate.ResolutionFence,
                profile.ResolutionFence));
    }

    public static FilterDefinition<StorageProfile> Default() =>
        Builders<StorageProfile>.Filter.Eq(profile => profile.IsDefault, true);

    public static FilterDefinition<StorageProfile> OtherDefaultsExcept(string storageProfileId) =>
        Builders<StorageProfile>.Filter.And(
            Default(),
            Builders<StorageProfile>.Filter.Ne(profile => profile.Id, storageProfileId));

    public static FilterDefinition<StorageProfile> AvailableForTarget(string deploymentTargetId) =>
        Builders<StorageProfile>.Filter.And(
            Builders<StorageProfile>.Filter.Eq(profile => profile.IsEnabled, true),
            Builders<StorageProfile>.Filter.ElemMatch(
                profile => profile.TargetMappings,
                mapping => mapping.DeploymentTargetId == deploymentTargetId));

    public static FilterDefinition<StorageProfile> AvailableAtResolution(
        StorageProfileResolutionFenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mapping = Builders<StorageProfileTargetMapping>.Filter.And(
            Builders<StorageProfileTargetMapping>.Filter.Eq(
                candidate => candidate.DeploymentTargetId,
                request.DeploymentTargetId),
            Builders<StorageProfileTargetMapping>.Filter.Eq(
                candidate => candidate.StorageClassName,
                request.StorageClassName));
        return Builders<StorageProfile>.Filter.And(
            ById(request.StorageProfileId),
            Builders<StorageProfile>.Filter.Eq(
                profile => profile.Revision,
                request.StorageProfileRevision),
            Builders<StorageProfile>.Filter.Eq(profile => profile.IsEnabled, true),
            Builders<StorageProfile>.Filter.ElemMatch(
                profile => profile.TargetMappings,
                mapping),
            Builders<StorageProfile>.Filter.Lt(
                profile => profile.ResolutionFence,
                long.MaxValue));
    }
}
