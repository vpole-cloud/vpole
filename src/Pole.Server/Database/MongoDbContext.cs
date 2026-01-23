using System.Reflection;
using System.Text.Json;
using MassTransit;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Server.Features.Common.Abstractions;
using Pole.Server.Features.Common.Outbox;
using Pole.Server.Features.DomainNames.Common;
using Pole.Server.Features.ResourceGroups.Common;
using Pole.Server.Features.WebServices.Common;
using EventExtensions = Pole.Server.Features.Common.Abstractions.EventExtensions;

namespace Pole.Server.Database;

public class MongoDbContext(
    IMongoClient client,
    IMongoDatabase database)
{
    public IMongoDatabase Database { get; } = database;
    public IMongoClient Client { get; } = client;
    public IMongoCollection<WebService> WebServices { get; } = database.GetCollection<WebService>("WebServices");
    public IMongoCollection<ResourceGroup> ResourceGroups { get; } = database.GetCollection<ResourceGroup>("ResourceGroups");
    public IMongoCollection<DnsRecord> DnsRecords { get; } = database.GetCollection<DnsRecord>("DnsRecords");
    public IMongoCollection<OutboxMessage> OutboxMessages { get; } = database.GetCollection<OutboxMessage>("OutboxMessages");

    public async Task StoreEventAsync<TEvent>(TEvent @event, IClientSessionHandle session, CancellationToken cancellationToken = default)
        where TEvent : BaseEvent
    {
        var eventType = typeof(TEvent);
        var entityNameAttribute = eventType.GetCustomAttribute<EntityNameAttribute>();

        var eventTypeName = entityNameAttribute?.EntityName ?? eventType.FullName ?? eventType.Name;

        var payloadJson = JsonSerializer.Serialize(@event, EventExtensions.SerializerOptions);

        var outboxMessage = new OutboxMessage
        {
            EventType = eventTypeName,
            PayloadJson = payloadJson,
            OccurredOn = @event.OccurredOn
        };

        await OutboxMessages.InsertOneAsync(session, outboxMessage, cancellationToken: cancellationToken);
    }

    public string GenerateNewId() => ObjectId.GenerateNewId().ToString();
}