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
public sealed class DeploymentTargetDomainAdmission(IDeploymentTargetDbContext dbContext)
{
    public async Task<Result> EnsureNoOverlappingPublicBaseDomainAsync(
        string targetId,
        string? publicBaseDomain,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (publicBaseDomain is null)
            return Result.Success();

        var candidate = DnsName.Create(publicBaseDomain);
        var fences = dbContext.Database.GetCollection<DeploymentTargetPublicBaseDomainAdmissionFence>(
            DeploymentTargetPublicBaseDomainAdmissionFence.CollectionName);
        await fences.UpdateOneAsync(
            session,
            fence => fence.Id == DeploymentTargetPublicBaseDomainAdmissionFence.SingletonId,
            Builders<DeploymentTargetPublicBaseDomainAdmissionFence>.Update
                .SetOnInsert(fence => fence.Id, DeploymentTargetPublicBaseDomainAdmissionFence.SingletonId)
                .Inc(fence => fence.Revision, 1),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

        var targets = await dbContext.DeploymentTargets
            .Find(
                session,
                DeploymentTargetFilters.WithConfiguredPublicBaseDomain())
            .ToListAsync(cancellationToken);
        foreach (var existing in targets)
        {
            if (string.Equals(existing.Id, targetId, StringComparison.Ordinal))
                continue;

            if (!DnsName.TryCreate(existing.WorkloadConfiguration.Ingress!.PublicBaseDomain, out var other))
                continue;
            var otherDomain = other!;
            if (candidate.IsSameOrDescendantOf(otherDomain) ||
                otherDomain.IsSameOrDescendantOf(candidate))
            {
                return Failures.Conflict(
                    "The public base domain overlaps another deployment target that has not been physically removed.");
            }
        }

        return Result.Success();
    }

    public async Task<Result> EnsureNoPublicDomainReferencesAsync(
        string targetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var reference = await dbContext.DeploymentTargetReferences
            .Find(
                session,
                DeploymentTargetReferenceFilters.DomainZonesForTarget(targetId))
            .Project(reference => reference.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return reference is null
            ? Result.Success()
            : Failures.Conflict(
                "The deployment target public base domain is still referenced by tenant domain zones.");
    }
}
