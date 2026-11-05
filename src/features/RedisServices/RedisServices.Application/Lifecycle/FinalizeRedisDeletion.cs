using MongoDB.Driver;
using Pole.Database;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Declarations;
using Pole.Features.RedisServices.Application.Mongo;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Lifecycle;

public sealed record RedisDeletionFinalizationConflict(string RedisId);

public sealed record RedisDeletionFinalized;

public union FinalizeRedisDeletionOutcome(
    RedisDeletionFinalized,
    RedisDeletionFinalizationConflict);

public delegate Task<FinalizeRedisDeletionOutcome> FinalizeRedisDeletion(
    string tenantId,
    string redisId,
    CancellationToken cancellationToken = default);

internal sealed class MongoFinalizeRedisDeletion(
    IMongoCollection<Redis> redisServices,
    GetResourceDeploymentFinalizationReadiness getDeploymentReadiness,
    PurgeDeletedResourceDeployments purgeDeployments,
    IResourceRegistrar resourceRegistrar,
    RunMongoOutcomeTransaction<FinalizeRedisDeletionOutcome>
        runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<FinalizeRedisDeletionOutcome> InvokeAsync(
        string tenantId,
        string redisId,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (transaction, transactionCancellationToken) =>
        {
            RedisPersistenceGuards.EnsureIdentity(tenantId, redisId);
            var redisFilter = RedisFilters.InLifecycle<DeletingRedisLifecycle>(
                tenantId,
                redisId);
            var redis = await redisServices
                .Find(transaction.Session, redisFilter)
                .FirstOrDefaultAsync(transactionCancellationToken);
            if (redis is null)
                return transaction.Commit(new RedisDeletionFinalized());

            var readiness = await getDeploymentReadiness(
                new GetResourceDeploymentFinalizationReadinessRequest(
                    TenantId: tenantId,
                    ResourceType: RedisResourceDeployment.ResourceType,
                    ResourceId: redisId),
                session: transaction.Session,
                cancellationToken: transactionCancellationToken);
            if (readiness is not ResourceDeploymentFinalizationReadiness.Ready)
            {
                return transaction.Commit(new RedisDeletionFinalized());
            }

            var deleted = await redisServices.DeleteOneAsync(
                transaction.Session,
                filter: redisFilter,
                cancellationToken: transactionCancellationToken);
            if (deleted.DeletedCount != 1)
            {
                return transaction.Abort(new RedisDeletionFinalizationConflict(redisId));
            }

            var registration = await resourceRegistrar.UnregisterAsync(
                new UnregisterResourceRequest(
                    TenantId: tenantId,
                    ResourceGroupId: redis.ResourceGroupId,
                    ResourceTypeId: RedisResourceDeployment.ResourceType,
                    ResourceId: redisId),
                session: transaction.Session,
                cancellationToken: transactionCancellationToken);
            if (registration is ResourceRegistrationMutationError)
            {
                return transaction.Abort(new RedisDeletionFinalizationConflict(redisId));
            }

            var purge = await purgeDeployments(
                new PurgeDeletedResourceDeploymentsRequest(
                    TenantId: tenantId,
                    ResourceType: RedisResourceDeployment.ResourceType,
                    ResourceId: redisId),
                session: transaction.Session,
                cancellationToken: transactionCancellationToken);
            if (purge is ResourceDeploymentFinalizationConflict)
            {
                return transaction.Abort(new RedisDeletionFinalizationConflict(redisId));
            }
            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(redis.ToDeleteEvent())],
                transactionCancellationToken);
            return transaction.Commit(new RedisDeletionFinalized());
        }, cancellationToken);
}
