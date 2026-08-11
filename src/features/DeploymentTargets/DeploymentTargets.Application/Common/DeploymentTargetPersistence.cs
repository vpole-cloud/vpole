using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed class DeploymentTargetPersistence(
    IDeploymentTargetDbContext dbContext,
    DeploymentTargetDomainAdmission domainAdmission)
{
    public async Task<Result<DeploymentTarget>> InsertAsync(
        DeploymentTarget target,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.RunInTransactionAsync<DeploymentTarget>(async (session, ct) =>
            {
                var admission = await domainAdmission.EnsureNoOverlappingPublicBaseDomainAsync(
                    target.Id,
                    target.WorkloadConfiguration.Ingress?.PublicBaseDomain,
                    session,
                    ct);
                if (admission is not ISuccess)
                    return (Failure)admission;

                await dbContext.DeploymentTargets.InsertOneAsync(
                    session,
                    target,
                    cancellationToken: ct);
                return target;
            }, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Failures.Conflict(
                "A deployment target with this Kubernetes cluster identity or public base domain already exists.");
        }
    }
}
