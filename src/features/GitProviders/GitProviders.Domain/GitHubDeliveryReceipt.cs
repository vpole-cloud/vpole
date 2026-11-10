using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitProviders.Domain;

public sealed record GitHubDeliveryReceipt(
    [property: BsonId] string Id,
    string PayloadHash,
    string EventType,
    DateTime ReceivedAtUtc
);
