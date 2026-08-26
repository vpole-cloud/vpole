using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

internal delegate Task<Result<Unit, DeploymentTargetPublicBaseDomainConflict>>
    EnsureNoOverlappingDeploymentTargetPublicBaseDomain(
        string deploymentTargetId,
        string? publicBaseDomain,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

/// <summary>
/// Serializes admission of DNS trees delegated to deployment targets. The fence prevents two
/// concurrent transactions from accepting ancestor and descendant public base domains.
/// </summary>
internal sealed class MongoEnsureNoOverlappingDeploymentTargetPublicBaseDomain(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    IMongoCollection<DeploymentTargetPublicBaseDomainAdmissionFence> fences)
{
    public async Task<Result<Unit, DeploymentTargetPublicBaseDomainConflict>> InvokeAsync(
        string deploymentTargetId,
        string? publicBaseDomain,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (publicBaseDomain is null)
            return new Ok<Unit>(Unit.Value);

        var candidate = DnsName.Create(publicBaseDomain);
        await fences.UpdateOneAsync(
            session,
            DeploymentTargetPublicBaseDomainAdmissionFenceFilters.Singleton(),
            Builders<DeploymentTargetPublicBaseDomainAdmissionFence>.Update
                .SetOnInsert(fence => fence.Id, DeploymentTargetPublicBaseDomainAdmissionFence.SingletonId)
                .Inc(fence => fence.Revision, 1),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

        var targets = await deploymentTargets
            .Find(
                session,
                DeploymentTargetFilters.WithConfiguredPublicBaseDomain())
            .ToListAsync(cancellationToken);
        foreach (var existing in targets)
        {
            if (string.Equals(existing.Id, deploymentTargetId, StringComparison.Ordinal))
                continue;

            var otherDomain = DnsName.Create(
                existing.WorkloadConfiguration.Ingress!.PublicBaseDomain ?? throw new InvalidOperationException(
                    "A deployment target with configured public ingress has no public base domain."));
            if (candidate.IsSameOrDescendantOf(otherDomain) ||
                otherDomain.IsSameOrDescendantOf(candidate))
            {
                return new Err<DeploymentTargetPublicBaseDomainConflict>(
                    new DeploymentTargetPublicBaseDomainConflict(deploymentTargetId));
            }
        }

        return new Ok<Unit>(Unit.Value);
    }
}
