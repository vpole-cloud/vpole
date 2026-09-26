using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Mutations;

public union PersistCreatedDeploymentTargetError(
    DeploymentTargetPublicBaseDomainConflict,
    DeploymentTargetIdentityConflict);

public union PersistCreatedDeploymentTargetOutcome(
    DeploymentTarget,
    PersistCreatedDeploymentTargetError);

public delegate Task<PersistCreatedDeploymentTargetOutcome>
    PersistCreatedDeploymentTarget(
        DeploymentTarget deploymentTarget,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedDeploymentTarget(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    EnsureNoOverlappingDeploymentTargetPublicBaseDomain ensureNoOverlappingPublicBaseDomain,
    RunMongoOutcomeTransaction<PersistCreatedDeploymentTargetOutcome> runMongoTransaction)
{
    public async Task<PersistCreatedDeploymentTargetOutcome> InvokeAsync(
        DeploymentTarget deploymentTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deploymentTarget);
        try
        {
            return await runMongoTransaction(async (transaction, ct) =>
            {
                var admitted = await ensureNoOverlappingPublicBaseDomain(
                    deploymentTarget.Id,
                    deploymentTarget.WorkloadConfiguration.Ingress?.PublicBaseDomain,
                    transaction.Session,
                    ct);
                if (admitted is DeploymentTargetPublicBaseDomainConflict admissionError)
                {
                    return transaction.Abort(
                        new PersistCreatedDeploymentTargetError(
                            admissionError));
                }

                await deploymentTargets.InsertOneAsync(
                    transaction.Session,
                    deploymentTarget,
                    cancellationToken: ct);
                return transaction.Commit(
                    deploymentTarget);
            }, cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new PersistCreatedDeploymentTargetError(
                    new DeploymentTargetIdentityConflict());
        }
    }
}
