using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Server.Features.Common.Abstractions;

public abstract record BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; init; } = ObjectId.GenerateNewId().ToString();
}