using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

internal static class DeploymentTargetObservationFilters
{
    internal static FilterDefinition<DeploymentTarget>
        CurrentEnabledConnection(string targetId, long connectionVersion) =>
        DeploymentTargetFilters.ById(targetId) & DeploymentTargetFilters.NotPendingDeletion() &
        Builders<DeploymentTarget>.Filter.Eq(target => target.Enabled, true) &
        Builders<DeploymentTarget>.Filter.Eq(target => target.Connection.Version, connectionVersion);
}
