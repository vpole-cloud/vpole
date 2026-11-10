using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Application.References;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Events;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Catalog;

public union PersistCreatedDeploymentTargetError(
    DeploymentTargetPublicBaseDomainConflict,
    DeploymentTargetIdentityConflict,
    DeploymentTargetTechnicalNameConflict);

public union PersistCreatedDeploymentTargetOutcome(DeploymentTarget, PersistCreatedDeploymentTargetError);

public delegate Task<PersistCreatedDeploymentTargetOutcome> PersistCreatedDeploymentTarget(
    DeploymentTarget deploymentTarget,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedDeploymentTarget(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    EnsureNoOverlappingDeploymentTargetPublicBaseDomain ensureNoOverlappingPublicBaseDomain,
    RunMongoOutcomeTransaction<PersistCreatedDeploymentTargetOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites
)
{
    public async Task<PersistCreatedDeploymentTargetOutcome> InvokeAsync(
        DeploymentTarget deploymentTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deploymentTarget);
        try
        {
            return await runMongoTransaction(
                async (transaction, ct) =>
                {
                    var admitted = await ensureNoOverlappingPublicBaseDomain(
                        deploymentTarget.Id,
                        deploymentTarget.WorkloadConfiguration.Ingress?.PublicBaseDomain,
                        transaction.Session,
                        ct);
                    if (admitted is DeploymentTargetPublicBaseDomainConflict admissionError)
                        return transaction.Abort(new PersistCreatedDeploymentTargetError(admissionError));

                    await deploymentTargets.InsertOneAsync(
                        transaction.Session,
                        deploymentTarget,
                        cancellationToken: ct);
                    await storeOutboxWrites(
                        transaction.Session,
                        [
                            OutboxWrite.Create(
                                new DeploymentTargetCatalogChangedEvent(
                                    deploymentTarget.Id,
                                    deploymentTarget.Revision)),
                        ],
                        ct);
                    return transaction.Commit(deploymentTarget);
                },
                cancellationToken);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                                                        exception,
                                                        DeploymentTargetIndexes.TechnicalName,
                                                        deploymentTargets.CollectionNamespace.CollectionName))
        {
            return new PersistCreatedDeploymentTargetError(
                new DeploymentTargetTechnicalNameConflict(deploymentTarget.TechnicalName));
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                                                        exception,
                                                        DeploymentTargetIndexes.ActivePublicBaseDomain,
                                                        deploymentTargets.CollectionNamespace.CollectionName))
        {
            return new PersistCreatedDeploymentTargetError(
                new DeploymentTargetPublicBaseDomainConflict(deploymentTarget.Id));
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                                                        exception,
                                                        DeploymentTargetIndexes.ClusterIdentity,
                                                        deploymentTargets.CollectionNamespace.CollectionName) ||
                                                    MongoDuplicateKey.IsForIdIndex(
                                                        exception,
                                                        deploymentTargets.CollectionNamespace.CollectionName))
        {
            return new PersistCreatedDeploymentTargetError(new DeploymentTargetIdentityConflict());
        }
    }
}
