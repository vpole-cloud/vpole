using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Common;

internal static class DeploymentTargetSettingsFilters
{
    public static FilterDefinition<DeploymentTargetSettings> Singleton() =>
        Builders<DeploymentTargetSettings>.Filter.Eq(
            settings => settings.Id,
            DeploymentTargetSettings.SingletonId);
}
