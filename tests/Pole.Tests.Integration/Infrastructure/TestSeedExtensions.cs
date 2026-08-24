using MongoDB.Driver;
using Pole.Database;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.WebServices.Domain;

namespace Pole.Tests.Integration.Infrastructure;

public static class TestSeedExtensions
{
    public static async Task<ResourceGroup> InsertResourceGroupAsync(this MongoTestStore dbContext, ResourceGroup resourceGroup)
    {
        var defaultScope = IsolationScopeFactory.CreateDefault(
            resourceGroup.TenantId,
            resourceGroup.Id,
            new IsolationScopeFactory.CreateOptions(
                resourceGroup.DefaultIsolationScopeId,
                resourceGroup.CreatedAtUtc));

        var result = await dbContext.RunInTransactionAsync(async (session, cancellationToken) =>
        {
            await dbContext.ResourceGroups.InsertOneAsync(
                session,
                resourceGroup,
                cancellationToken: cancellationToken);
            await dbContext.IsolationScopes.InsertOneAsync(
                session,
                defaultScope,
                cancellationToken: cancellationToken);
            return Pole.ResultType.Result.Success();
        });
        if (result is not Pole.ResultType.ISuccess)
            throw new InvalidOperationException("Failed to seed a resource group with its default isolation scope.");

        return resourceGroup;
    }

    public static async Task<WebService> InsertWebServiceAsync(this MongoTestStore dbContext, WebService webService)
    {
        await dbContext.Database.WebServices().InsertOneAsync(webService);
        return webService;
    }

    public static async Task<Redis> InsertRedisAsync(this MongoTestStore dbContext, Redis redis)
    {
        await dbContext.Database.RedisServices().InsertOneAsync(redis);
        return redis;
    }

    public static async Task<long> CountOutboxMessagesAsync(this MongoTestStore dbContext, string eventType)
    {
        return await dbContext.OutboxMessages.CountDocumentsAsync(message => message.EventType == eventType);
    }
}
