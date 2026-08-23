using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

/// <summary>
/// Target-owned contextual guards for the DNS tree delegated to a deployment target. The target
/// reference registry is the shared fence: Domains registers a <c>domain-zone</c> reference in
/// the same transaction as its zone. Every public deployment has a generated zone, so that
/// reference is the exact public-base-domain mutation guard without coupling to deployments.
/// </summary>
internal sealed class DeploymentTargetDomainAdmission(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    IMongoCollection<DeploymentTargetReference> references,
    IMongoCollection<DeploymentTargetPublicBaseDomainAdmissionFence> fences)
{
    public async Task<Result<Unit, DeploymentTargetPublicBaseDomainConflict>>
        EnsureNoOverlappingPublicBaseDomainAsync(
        string targetId,
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
            if (string.Equals(existing.Id, targetId, StringComparison.Ordinal))
                continue;

            var otherDomain = DnsName.Create(
                existing.WorkloadConfiguration.Ingress!.PublicBaseDomain ?? throw new InvalidOperationException(
                    "A deployment target with configured public ingress has no public base domain."));
            if (candidate.IsSameOrDescendantOf(otherDomain) ||
                otherDomain.IsSameOrDescendantOf(candidate))
            {
                return new Err<DeploymentTargetPublicBaseDomainConflict>(
                    new DeploymentTargetPublicBaseDomainConflict(targetId));
            }
        }

        return new Ok<Unit>(Unit.Value);
    }

    public async Task<Result<Unit, DeploymentTargetPublicBaseDomainReferenced>>
        EnsureNoPublicDomainReferencesAsync(
        string targetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var reference = await references
            .Find(
                session,
                DeploymentTargetReferenceFilters.DomainZonesForTarget(targetId))
            .Project(reference => reference.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return reference is null
            ? new Ok<Unit>(Unit.Value)
            : new Err<DeploymentTargetPublicBaseDomainReferenced>(
                new DeploymentTargetPublicBaseDomainReferenced(targetId));
    }
}
