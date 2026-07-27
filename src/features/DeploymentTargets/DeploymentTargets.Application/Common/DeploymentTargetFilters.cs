using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Common;

internal static class DeploymentTargetFilters
{
    public static FilterDefinition<DeploymentTarget> RevisionFilter(
        DeploymentTarget target) =>
        Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Eq(item => item.Id, target.Id),
            Builders<DeploymentTarget>.Filter.Eq(item => item.Revision, target.Revision));
}
