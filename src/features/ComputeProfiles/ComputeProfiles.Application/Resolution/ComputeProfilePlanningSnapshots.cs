using Pole.Features.ComputeProfiles.Application.Mongo;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Resolution;

internal sealed class MongoComputeProfilePlanningSnapshots(
    IMongoCollection<ComputeProfile> profiles,
    IMongoCollection<ComputeProfileRevision> revisions,
    FenceResolvedComputeProfiles fence)
{
    public async Task<ResolveComputeProfilePlanningSnapshotOutcome> ReadAsync(
        ComputeProfilePlanningRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        RequireTransaction(session);
        MongoResolveComputeProfiles.EnsureValid(new ComputeProfileBatchResolutionRequest(
            request.TenantId, request.ResourceType,
            [new(request.DeploymentTargetId, request.ComputeProfileId, request.Revision)]));
        request = request with
        {
            ComputeProfileId = request.ComputeProfileId.Trim(),
            DeploymentTargetId = request.DeploymentTargetId.Trim()
        };

        var profile = await profiles.Find(session, ComputeProfileFilters.ById(request.ComputeProfileId))
            .FirstOrDefaultAsync(ct);
        if (profile is null)
            return new ComputeProfilePlanningUnavailable(new ComputeProfileNotFound(request.ComputeProfileId));

        var unavailable = MongoResolveComputeProfiles.ValidateAvailability(profile, request.TenantId,
            request.ResourceType, request.DeploymentTargetId);
        if (unavailable is not null)
            return new ComputeProfilePlanningUnavailable(unavailable);

        var policyRevision = profile.Revision;
        if (profile.Revision != request.Revision)
        {
            var historical = await revisions.Find(session,
                ComputeProfileRevisionFilters.Identity(profile.Id, request.Revision)).FirstOrDefaultAsync(ct);
            if (historical is null || !historical.Snapshot.TargetMappings.Any(item =>
                    item.DeploymentTargetId == request.DeploymentTargetId))
                return new ComputeProfilePlanningUnavailable(new ComputeProfileUnavailable(profile.Id,
                    ComputeProfileUnavailableReason.RevisionUnavailable));
            profile = historical.Snapshot;
        }

        var mapping = profile.TargetMappings.Single(item => item.DeploymentTargetId == request.DeploymentTargetId);
        return new ComputeProfilePlanningSnapshot(request, policyRevision,
            MongoResolveComputeProfiles.ToResolved(profile, mapping));
    }

    public async Task<FenceComputeProfilePlanningSnapshotOutcome> FenceAsync(
        ComputeProfilePlanningSnapshot request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var read = await ReadAsync(request.Request, session, ct);
        if (read is not ComputeProfilePlanningSnapshot current || current.PolicyRevision != request.PolicyRevision)
            return new ComputeProfilePlanningFenceRejected();

        var input = request.Request;
        var result = await fence([new(input.ComputeProfileId, input.Revision, input.DeploymentTargetId,
            input.TenantId, input.ResourceType, PinnedRevision: true)], session, ct);
        return result is ComputeProfilesFenced
            ? new ComputeProfilePlanningFenced()
            : new ComputeProfilePlanningFenceRejected();
    }

    private static void RequireTransaction(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Planning snapshots require an active transaction.");
    }
}
