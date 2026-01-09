using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Server.Models;

public abstract record BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; init; } = ObjectId.GenerateNewId().ToString();
}