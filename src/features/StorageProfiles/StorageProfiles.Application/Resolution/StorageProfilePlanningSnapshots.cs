using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Mongo;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Resolution;

internal sealed class MongoStorageProfilePlanningSnapshots(
    IMongoCollection<StorageProfile> profiles,
    IMongoCollection<StorageProfileRevision> revisions,
    DiscoverKubernetesStorageClassNames discover)
{
    public async Task<ResolveStorageProfilePlanningSnapshotOutcome> ReadAsync(
        StorageProfilePlanningRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        RequireTransaction(session);
        var input = new StorageProfileResolutionRequest(request.StorageProfileId, request.DeploymentTargetId,
            request.TenantId, request.ResourceType, request.SizeGb, request.Revision);
        StorageProfileResolution.EnsureValid(input);
        var current = await profiles.Find(session, StorageProfileFilters.ById(request.StorageProfileId))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new StorageProfilePlanningUnavailable(new StorageProfileNotFound(request.StorageProfileId));

        var unavailable = StorageProfileResolution.ValidatePolicy(current, input);
        if (unavailable is not null)
            return new StorageProfilePlanningUnavailable(unavailable.Value);

        var selected = current;
        if (current.Revision != request.Revision)
        {
            var historical = await revisions.Find(session,
                StorageProfileRevisionFilters.Identity(current.Id, request.Revision)).FirstOrDefaultAsync(ct);
            if (historical is null)
                return new StorageProfilePlanningUnavailable(
                    new StorageProfileRevisionUnavailable(current.Id, request.Revision));

            selected = historical.Snapshot;
        }

        var resolved = await StorageProfileResolution.ResolveAsync(selected, input, discover, ct);
        return resolved switch
        {
            ResolvedStorageProfile storage => new StorageProfilePlanningSnapshot(request, current.Revision, storage),
            StorageProfileResolutionError error => new StorageProfilePlanningUnavailable(error)
        };
    }

    public async Task<FenceStorageProfilePlanningSnapshotOutcome> FenceAsync(
        StorageProfilePlanningSnapshot request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var read = await ReadAsync(request.Request, session, ct);
        if (read is not StorageProfilePlanningSnapshot current || current.PolicyRevision != request.PolicyRevision ||
            current.Storage != request.Storage)
            return new StorageProfilePlanningFenceRejected();

        var fenced = await profiles.UpdateOneAsync(session,
            StorageProfileFilters.AtPolicy(request.Request.StorageProfileId, request.PolicyRevision),
            Builders<StorageProfile>.Update.Inc(profile => profile.ResolutionFence, 1), cancellationToken: ct);
        return fenced.ModifiedCount == 1
            ? new StorageProfilePlanningFenced()
            : new StorageProfilePlanningFenceRejected();
    }

    private static void RequireTransaction(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Storage planning requires an active transaction.");
    }
}
