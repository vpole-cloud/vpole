using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Placement;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.StorageProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Common;

public sealed class StorageProfileResolutionFence(
    IStorageProfileDbContext dbContext)
    : IResourceDeploymentStorageConfigurationFence
{
    public async Task<Result> FenceAsync(
        string deploymentTargetId,
        ResolvedStorageConfiguration storage,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        EnsureTransaction(session);
        if (!ObjectId.TryParse(deploymentTargetId, out _))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Placement.DeploymentTargetId"] =
                    ["Deployment target ID must be a MongoDB ObjectId."]
            });
        }

        var mappingFilter =
            Builders<StorageProfileTargetMapping>.Filter.And(
                Builders<StorageProfileTargetMapping>.Filter.Eq(
                    mapping => mapping.DeploymentTargetId,
                    deploymentTargetId),
                Builders<StorageProfileTargetMapping>.Filter.Eq(
                    mapping => mapping.StorageClassName,
                    storage.StorageClassName));
        var update = await dbContext.StorageProfiles.UpdateOneAsync(
            session,
            Builders<StorageProfile>.Filter.And(
                Builders<StorageProfile>.Filter.Eq(
                    profile => profile.Id,
                    storage.StorageProfileId),
                Builders<StorageProfile>.Filter.Eq(
                    profile => profile.Revision,
                    storage.StorageProfileRevision),
                Builders<StorageProfile>.Filter.Eq(
                    profile => profile.IsEnabled,
                    true),
                Builders<StorageProfile>.Filter.ElemMatch(
                    profile => profile.TargetMappings,
                    mappingFilter),
                Builders<StorageProfile>.Filter.Lt(
                    profile => profile.ResolutionFence,
                    long.MaxValue)),
            Builders<StorageProfile>.Update.Inc(
                profile => profile.ResolutionFence,
                1),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == 1
            ? Result.Success()
            : Failures.Conflict(
                "The storage profile target mapping changed or is no longer available.");
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Storage-profile resolution fencing must run inside the logical resource transaction.");
        }
    }
}
