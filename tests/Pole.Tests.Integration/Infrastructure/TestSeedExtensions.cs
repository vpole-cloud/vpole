using MongoDB.Driver;
using Pole.Database;
using Pole.Domain.Models;

namespace Pole.Tests.Integration.Infrastructure;

public static class TestSeedExtensions
{
    public static async Task<ResourceGroup> InsertResourceGroupAsync(this MongoDbContext dbContext, ResourceGroup resourceGroup)
    {
        await dbContext.ResourceGroups.InsertOneAsync(resourceGroup);
        return resourceGroup;
    }

    public static async Task<DnsRecord> InsertDnsRecordAsync(this MongoDbContext dbContext, DnsRecord dnsRecord)
    {
        await dbContext.DnsRecords.InsertOneAsync(dnsRecord);
        return dnsRecord;
    }

    public static async Task<WebService> InsertWebServiceAsync(this MongoDbContext dbContext, WebService webService)
    {
        await dbContext.WebServices.InsertOneAsync(webService);
        return webService;
    }

    public static async Task<Redis> InsertRedisAsync(this MongoDbContext dbContext, Redis redis)
    {
        await dbContext.RedisServices.InsertOneAsync(redis);
        return redis;
    }

    public static async Task<long> CountOutboxMessagesAsync(this MongoDbContext dbContext, string eventType)
    {
        return await dbContext.OutboxMessages.CountDocumentsAsync(message => message.EventType == eventType);
    }
}
