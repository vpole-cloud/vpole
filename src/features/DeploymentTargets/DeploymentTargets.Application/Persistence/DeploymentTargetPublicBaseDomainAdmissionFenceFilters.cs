using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

internal static class DeploymentTargetPublicBaseDomainAdmissionFenceFilters
{
    public static FilterDefinition<DeploymentTargetPublicBaseDomainAdmissionFence> Singleton() =>
        Builders<DeploymentTargetPublicBaseDomainAdmissionFence>.Filter.Eq(
            fence => fence.Id,
            DeploymentTargetPublicBaseDomainAdmissionFence.SingletonId);
}
