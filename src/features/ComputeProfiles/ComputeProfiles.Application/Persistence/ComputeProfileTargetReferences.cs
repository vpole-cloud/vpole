using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal static class ComputeProfileTargetReferences
{
    public const string OwnerType = "compute-profile";

    public static Task<Result> ApplyChangesAsync(
        IDeploymentTargetReferenceAdmission referenceAdmission,
        ComputeProfile profile,
        IEnumerable<string> addedDeploymentTargetIds,
        IEnumerable<string> removedDeploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addedDeploymentTargetIds);
        ArgumentNullException.ThrowIfNull(removedDeploymentTargetIds);
        return referenceAdmission.ApplyChangesAsync(
            new DeploymentTargetReferenceOwner(OwnerType, profile.Id),
            addedDeploymentTargetIds.Distinct(StringComparer.Ordinal).ToArray(),
            removedDeploymentTargetIds.Distinct(StringComparer.Ordinal).ToArray(),
            session,
            cancellationToken);
    }
}
