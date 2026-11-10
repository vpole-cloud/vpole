using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.ComputeProfiles.Application.Resolution;

internal static class ComputeProfileTargetReferences
{
    public const string OwnerType = DeploymentTargetReferenceOwnerTypes.ComputeProfile;

    public static Task<ApplyDeploymentTargetReferenceChangesOutcome> ApplyChangesAsync(
        ApplyDeploymentTargetReferenceChanges applyChanges,
        ComputeProfile profile,
        IEnumerable<string> addedDeploymentTargetIds,
        IEnumerable<string> removedDeploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addedDeploymentTargetIds);
        ArgumentNullException.ThrowIfNull(removedDeploymentTargetIds);
        return applyChanges(
            new ApplyDeploymentTargetReferenceChangesRequest(
                Owner: new DeploymentTargetReferenceOwner(OwnerType, profile.Id),
                AddedDeploymentTargetIds: addedDeploymentTargetIds.Distinct(StringComparer.Ordinal).ToArray(),
                RemovedDeploymentTargetIds: removedDeploymentTargetIds.Distinct(StringComparer.Ordinal).ToArray()),
            session: session,
            cancellationToken: cancellationToken);
    }
}
