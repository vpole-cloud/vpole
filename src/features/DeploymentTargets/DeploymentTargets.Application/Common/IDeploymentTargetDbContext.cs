using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Common;

public interface IDeploymentTargetDbContext : IMongoDbContext
{
    IMongoCollection<DeploymentTarget> DeploymentTargets { get; }
    IMongoCollection<DeploymentTargetReference> DeploymentTargetReferences { get; }
    IMongoCollection<DeploymentTargetSettings> DeploymentTargetSettings { get; }
}
