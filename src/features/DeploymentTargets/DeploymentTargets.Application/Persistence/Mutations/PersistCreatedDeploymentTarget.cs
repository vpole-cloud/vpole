using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Mutations;

public union PersistCreatedDeploymentTargetError(
    DeploymentTargetPublicBaseDomainConflict,
    DeploymentTargetIdentityConflict);

public delegate Task<Result<DeploymentTarget, PersistCreatedDeploymentTargetError>>
    PersistCreatedDeploymentTarget(
        DeploymentTarget deploymentTarget,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedDeploymentTarget(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    EnsureNoOverlappingDeploymentTargetPublicBaseDomain ensureNoOverlappingPublicBaseDomain,
    RunMongoOutcomeTransaction<Result<
        DeploymentTarget,
        PersistCreatedDeploymentTargetError>> runMongoTransaction)
{
    public async Task<Result<DeploymentTarget, PersistCreatedDeploymentTargetError>> InvokeAsync(
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
                if (!admitted.TryGetValue(out _, out var admissionError))
                {
                    return transaction.AbortError(
                        new PersistCreatedDeploymentTargetError(
                            admissionError));
                }

                await deploymentTargets.InsertOneAsync(
                    transaction.Session,
                    deploymentTarget,
                    cancellationToken: ct);
                return transaction.Commit(
                    new Ok<DeploymentTarget>(deploymentTarget));
            }, cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new Err<PersistCreatedDeploymentTargetError>(
                new PersistCreatedDeploymentTargetError(
                    new DeploymentTargetIdentityConflict()));
        }
    }
}
