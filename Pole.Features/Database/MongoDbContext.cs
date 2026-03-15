using System.Reflection;
using System.Text.Json;
using MassTransit;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Domain.Models;
using Pole.Features.Common.Outbox;
using Pole.Features.Core.Abstractions;
using Pole.Features.Core.Outbox;
using Pole.Features.DomainNames.Common;
using Pole.Features.RedisServices.Common;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.WebServices.Common;
using EventExtensions = Pole.Features.Core.Abstractions.EventExtensions;

namespace Pole.Features.Database;

public class MongoDbContext(
    IMongoClient client,
    IMongoDatabase database)
{
    public IMongoDatabase Database { get; } = database;
    public IMongoClient Client { get; } = client;
    public IMongoCollection<WebService> WebServices { get; } = database.GetCollection<WebService>("WebServices");
    public IMongoCollection<Redis> RedisServices { get; } = database.GetCollection<Redis>("RedisServices");
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