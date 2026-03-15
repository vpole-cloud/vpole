using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.Core.Outbox;

public record OutboxMessage
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; init; } = ObjectId.GenerateNewId().ToString();
    
    public required string EventType { get; init; }
    
    public required string PayloadJson { get; init; }
    
    public DateTime OccurredOn { get; init; } = DateTime.UtcNow;
    
    public DateTime? ProcessedOn { get; init; }
    
    public int RetryCount { get; init; } = 0;
    
    public string? LastError { get; init; }
}
