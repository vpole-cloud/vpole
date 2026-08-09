using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Persistence;

public delegate Task<Result> FinalizeRedisDeletion(
    string tenantId,
    string redisId,
    CancellationToken cancellationToken = default);

internal sealed class MongoFinalizeRedisDeletion(
    IMongoCollection<Redis> redisServices,
    IResourceDeploymentFinalizationService deploymentFinalization,
    RunMongoTransaction runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<Result> InvokeAsync(
        string tenantId,
        string redisId,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (session, transactionCancellationToken) =>
        {
            var redisFilter = RedisFilters.InLifecycle<DeletingRedisLifecycle>(
                tenantId,
                redisId);
            var redis = await redisServices
                .Find(session, redisFilter)
                .FirstOrDefaultAsync(transactionCancellationToken);
            if (redis is null)
                return Result.Success();

            var readiness = await deploymentFinalization.GetReadinessAsync(
                tenantId,
                RedisResourceDeployment.ResourceType,
                redisId,
                session,
                transactionCancellationToken);
            if (readiness is not ResourceDeploymentFinalizationReadiness.Ready)
            {
                return Result.Success();
            }

            var deleted = await redisServices.DeleteOneAsync(
                session,
                filter: redisFilter,
                cancellationToken: transactionCancellationToken);
            if (deleted.DeletedCount != 1)
                return Result.Success();

            var purge = await deploymentFinalization.PurgeDeletedAsync(
                tenantId,
                RedisResourceDeployment.ResourceType,
                redisId,
                session,
                transactionCancellationToken);
            if (purge is not ISuccess)
                return (Failure)purge;
            await storeOutboxWrites(
                session,
                [OutboxWrite.Create(redis.ToDeleteEvent())],
                transactionCancellationToken);
            return Result.Success();
        }, cancellationToken);
}
