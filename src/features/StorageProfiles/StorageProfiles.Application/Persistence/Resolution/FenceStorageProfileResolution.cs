using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Persistence.Resolution;

internal sealed class MongoFenceStorageProfileResolution(
    IMongoCollection<StorageProfile> storageProfiles)
{
    public async Task<FenceStorageProfileResolutionOutcome> InvokeAsync(
        StorageProfileResolutionFenceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTransaction(session);
        EnsureValidRequest(request);

        var fenced = await storageProfiles.UpdateOneAsync(
            session,
            StorageProfileFilters.AvailableAtResolution(request),
            Builders<StorageProfile>.Update.Inc(profile => profile.ResolutionFence, 1),
            cancellationToken: cancellationToken);
        return fenced.ModifiedCount == 1
            ? new StorageProfileResolutionFenced()
            : new StorageProfileResolutionFenceRejected(request.StorageProfileId);
    }

    private static void EnsureValidRequest(StorageProfileResolutionFenceRequest request)
    {
        if (!ObjectId.TryParse(request.StorageProfileId, out _) ||
            !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
            request.StorageProfileRevision < 1 ||
            !StorageProfileTargetMapping.IsValidStorageClassName(request.StorageClassName))
        {
            throw new ArgumentException(
                "A valid resolved storage profile is required.",
                nameof(request));
        }
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
