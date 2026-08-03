using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Domain;
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
    IMongoCollection<ResourceDeployment> resourceDeployments,
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

            var deploymentFilter = Builders<ResourceDeployment>.Filter.And(
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.TenantId,
                    tenantId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.ResourceType,
                    RedisResourceDeployment.ResourceType.Value),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.ResourceId,
                    redisId));
            var deployments = await resourceDeployments
                .Find(session, deploymentFilter)
                .ToListAsync(transactionCancellationToken);
            if (deployments.Count == 0 ||
                deployments.Any(deployment =>
                    deployment.State is not DeletedResourceDeploymentState))
            {
                return Result.Success();
            }

            var deleted = await redisServices.DeleteOneAsync(
                session,
                filter: redisFilter,
                cancellationToken: transactionCancellationToken);
            if (deleted.DeletedCount != 1)
                return Result.Success();

            await resourceDeployments.DeleteManyAsync(
                session,
                filter: deploymentFilter,
                cancellationToken: transactionCancellationToken);
            await storeOutboxWrites(
                session,
                [OutboxWrite.Create(redis.ToDeleteEvent())],
                transactionCancellationToken);
            return Result.Success();
        }, cancellationToken);
}
