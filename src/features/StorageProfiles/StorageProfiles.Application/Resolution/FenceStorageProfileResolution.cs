using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Mongo;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Resolution;

internal sealed class MongoFenceStorageProfileResolution(
    IMongoCollection<StorageProfile> storageProfiles,
    IMongoCollection<StorageProfileRevision> revisions
)
{
    public async Task<FenceStorageProfileResolutionOutcome> InvokeAsync(
        StorageProfileResolutionFenceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTransaction(session);
        EnsureValidRequest(request);

        if (request.PolicyRevision is { } policyRevision)
        {
            var current = await storageProfiles.Find(session, StorageProfileFilters.ById(request.StorageProfileId))
                .FirstOrDefaultAsync(cancellationToken);
            if (current is null || current.Revision != policyRevision || !current.IsEnabled ||
                !current.TargetMappings.Any(mapping => mapping.DeploymentTargetId == request.DeploymentTargetId))
                return new StorageProfileResolutionFenceRejected(request.StorageProfileId);

            var selected = current;
            if (current.Revision != request.StorageProfileRevision)
            {
                var historical = await revisions.Find(
                        session,
                        StorageProfileRevisionFilters.Identity(current.Id, request.StorageProfileRevision))
                    .FirstOrDefaultAsync(cancellationToken);
                if (historical is null)
                    return new StorageProfileResolutionFenceRejected(request.StorageProfileId);

                selected = historical.Snapshot;
            }

            if (!selected.TargetMappings.Any(mapping => mapping.DeploymentTargetId == request.DeploymentTargetId &&
                    mapping.StorageClassName == request.StorageClassName))
                return new StorageProfileResolutionFenceRejected(request.StorageProfileId);

            var pinnedFence = await storageProfiles.UpdateOneAsync(
                session,
                StorageProfileFilters.AtSnapshot(current) &
                StorageProfileFilters.AtPolicy(current.Id, current.Revision),
                Builders<StorageProfile>.Update.Inc(profile => profile.ResolutionFence, 1),
                cancellationToken: cancellationToken);
            return pinnedFence.ModifiedCount == 1
                ? new StorageProfileResolutionFenced()
                : new StorageProfileResolutionFenceRejected(request.StorageProfileId);
        }

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
            !ObjectId.TryParse(request.DeploymentTargetId, out _) || request.StorageProfileRevision < 1 ||
            request.PolicyRevision is <= 0 ||
            !StorageProfileTargetMapping.IsValidStorageClassName(request.StorageClassName))
            throw new ArgumentException("A valid resolved storage profile is required.", nameof(request));
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
            throw new InvalidOperationException(
                "Storage-profile resolution fencing must run inside the logical resource transaction.");
    }
}
