using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence.References;

public delegate Task<Result<Unit, DeploymentTargetHasDurableReferences>>
    EnsureNoDeploymentTargetReferences(
        string deploymentTargetId,
        CancellationToken cancellationToken = default);

internal sealed class MongoEnsureNoDeploymentTargetReferences(
    IMongoCollection<DeploymentTargetReference> references)
{
    public async Task<Result<Unit, DeploymentTargetHasDurableReferences>> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var reference = await references
            .Find(DeploymentTargetReferenceFilters.ForTarget(deploymentTargetId))
            .Project(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return reference is null
            ? new Ok<Unit>(Unit.Value)
            : new Err<DeploymentTargetHasDurableReferences>(
                new DeploymentTargetHasDurableReferences(deploymentTargetId));
    }
}
