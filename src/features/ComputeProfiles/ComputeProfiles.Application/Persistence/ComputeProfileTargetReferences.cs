using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

internal static class ComputeProfileTargetReferences
{
    public const string OwnerType = "compute-profile";

    public static async Task<Result> RegisterAsync(
        IDeploymentTargetReferenceAdmission referenceAdmission,
        ComputeProfile profile,
        IEnumerable<string> deploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        foreach (var deploymentTargetId in deploymentTargetIds.Distinct(StringComparer.Ordinal))
        {
            var registered = await referenceAdmission.RegisterAsync(
                deploymentTargetId,
                new DeploymentTargetReferenceOwner(OwnerType, profile.Id),
                session,
                cancellationToken);
            if (registered is not ISuccess)
                return (Failure)registered;
        }

        return Result.Success();
    }

    public static async Task<Result> UnregisterAsync(
        IDeploymentTargetReferenceAdmission referenceAdmission,
        ComputeProfile profile,
        IEnumerable<string> deploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        foreach (var deploymentTargetId in deploymentTargetIds.Distinct(StringComparer.Ordinal))
        {
            var unregistered = await referenceAdmission.UnregisterAsync(
                deploymentTargetId,
                new DeploymentTargetReferenceOwner(OwnerType, profile.Id),
                session,
                cancellationToken);
            if (unregistered is not ISuccess)
                return (Failure)unregistered;
        }

        return Result.Success();
    }
}
