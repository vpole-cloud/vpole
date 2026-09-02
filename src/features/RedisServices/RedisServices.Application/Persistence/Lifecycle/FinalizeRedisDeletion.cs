using MongoDB.Driver;
using Pole.Database;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Persistence.Lifecycle;

public sealed record RedisDeletionFinalizationConflict(string RedisId);

public delegate Task<Result<Unit, RedisDeletionFinalizationConflict>> FinalizeRedisDeletion(
    string tenantId,
    string redisId,
    CancellationToken cancellationToken = default);

internal sealed class MongoFinalizeRedisDeletion(
    IMongoCollection<Redis> redisServices,
    GetResourceDeploymentFinalizationReadiness getDeploymentReadiness,
    PurgeDeletedResourceDeployments purgeDeployments,
    UnregisterResource unregisterResource,
    RunMongoOutcomeTransaction<Result<Unit, RedisDeletionFinalizationConflict>>
        runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<Result<Unit, RedisDeletionFinalizationConflict>> InvokeAsync(
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
                return transaction.Commit(new Ok<Unit>(Unit.Value));

            var readiness = await getDeploymentReadiness(
                tenantId,
                RedisResourceDeployment.ResourceType,
                redisId,
                transaction.Session,
                transactionCancellationToken);
            if (readiness is not ResourceDeploymentFinalizationReadiness.Ready)
            {
                return transaction.Commit(new Ok<Unit>(Unit.Value));
            }

            var deleted = await redisServices.DeleteOneAsync(
                transaction.Session,
                filter: redisFilter,
                cancellationToken: transactionCancellationToken);
            if (deleted.DeletedCount != 1)
            {
                return transaction.Abort(new Err<RedisDeletionFinalizationConflict>(
                    new RedisDeletionFinalizationConflict(redisId)));
            }

            var registration = await unregisterResource(
                tenantId,
                redis.ResourceGroupId,
                RedisResourceDeployment.ResourceType,
                redisId,
                transaction.Session,
                transactionCancellationToken);
            if (!registration.TryGetValue(out _, out _))
            {
                return transaction.Abort(new Err<RedisDeletionFinalizationConflict>(
                    new RedisDeletionFinalizationConflict(redisId)));
            }

            var purge = await purgeDeployments(
                tenantId,
                RedisResourceDeployment.ResourceType,
                redisId,
                transaction.Session,
                transactionCancellationToken);
            if (!purge.TryGetValue(out _, out _))
            {
                return transaction.Abort(new Err<RedisDeletionFinalizationConflict>(
                    new RedisDeletionFinalizationConflict(redisId)));
            }
            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(redis.ToDeleteEvent())],
                transactionCancellationToken);
            return transaction.Commit(new Ok<Unit>(Unit.Value));
        }, cancellationToken);
}
