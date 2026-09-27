using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.References;

internal sealed record DeploymentTargetPublicBaseDomainUnreferenced;
internal union EnsureDeploymentTargetPublicBaseDomainUnreferencedOutcome(
    DeploymentTargetPublicBaseDomainUnreferenced,
    DeploymentTargetPublicBaseDomainReferenced);

internal delegate Task<EnsureDeploymentTargetPublicBaseDomainUnreferencedOutcome>
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
    public async Task<EnsureDeploymentTargetPublicBaseDomainUnreferencedOutcome> InvokeAsync(
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
            ? new DeploymentTargetPublicBaseDomainUnreferenced()
            : new DeploymentTargetPublicBaseDomainReferenced(deploymentTargetId);
    }
}
