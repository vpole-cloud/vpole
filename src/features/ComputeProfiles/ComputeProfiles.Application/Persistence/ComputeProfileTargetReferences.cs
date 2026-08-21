using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal static class ComputeProfileTargetReferences
{
public const string OwnerType = "compute-profile";

    public static Task<Result<Unit, DeploymentTargetReferenceChangeRejected>>
        ApplyChangesAsync(
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
            new DeploymentTargetReferenceOwner(OwnerType, profile.Id),
            addedDeploymentTargetIds.Distinct(StringComparer.Ordinal).ToArray(),
            removedDeploymentTargetIds.Distinct(StringComparer.Ordinal).ToArray(),
            session,
            cancellationToken);
    }
}
