using MongoDB.Driver;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Testing;
using Pole.Features.WebServices.Domain;

namespace Pole.Tests.Integration.Infrastructure;

public static class TestSeedExtensions
{
    public static async Task<ResourceGroup> InsertResourceGroupAsync(
        this MongoTestStore dbContext,
        ResourceGroup resourceGroup)
    {
        await new ResourceGroupTestData(dbContext.Database).SeedAsync(resourceGroup);

        return resourceGroup;
    }

    public static async Task<WebService> InsertWebServiceAsync(this MongoTestStore dbContext, WebService webService)
    {
        await dbContext.Database.WebServices().InsertOneAsync(webService);
        return webService;
    }

    public static async Task<long> CountOutboxMessagesAsync(
        this MongoTestStore dbContext,
        string? tenantId,
        string eventType)
    {
        return await dbContext.OutboxMessages.CountDocumentsAsync(message =>
            message.TenantId == tenantId && message.EventType == eventType);
    }

    public static Task<long> CountAllOutboxMessagesAsync(this MongoTestStore dbContext, string eventType) =>
        dbContext.OutboxMessages.CountDocumentsAsync(message => message.EventType == eventType);
}
