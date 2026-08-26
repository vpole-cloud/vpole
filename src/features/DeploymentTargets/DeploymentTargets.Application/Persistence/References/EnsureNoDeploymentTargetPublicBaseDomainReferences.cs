using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence.References;

internal delegate Task<Result<Unit, DeploymentTargetPublicBaseDomainReferenced>>
    EnsureNoDeploymentTargetPublicBaseDomainReferences(
        string deploymentTargetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

/// <summary>
/// The target-reference registry is the public-base-domain mutation guard. Domains registers a
/// <c>domain-zone</c> reference in the same transaction as its generated zone.
/// </summary>
internal sealed class MongoEnsureNoDeploymentTargetPublicBaseDomainReferences(
    IMongoCollection<DeploymentTargetReference> references)
{
    public async Task<Result<Unit, DeploymentTargetPublicBaseDomainReferenced>> InvokeAsync(
        string deploymentTargetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var reference = await references
            .Find(
                session,
                DeploymentTargetReferenceFilters.DomainZonesForTarget(deploymentTargetId))
            .Project(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return reference is null
            ? new Ok<Unit>(Unit.Value)
            : new Err<DeploymentTargetPublicBaseDomainReferenced>(
                new DeploymentTargetPublicBaseDomainReferenced(deploymentTargetId));
    }
}
