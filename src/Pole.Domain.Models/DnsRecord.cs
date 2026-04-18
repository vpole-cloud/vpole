using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Domain.Models;

public record DnsRecord(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string Name,
    ResourceType ResourceType,
    string ResourceId,
    string ResourceName,
    string GroupId
);

public static class DnsRecordFactory
{
    public sealed record DnsRecordCreateOptions(
        string Id
    );

    public static DnsRecordCreateOptions DefaultCreateOptions =>
        new(
            Id: ObjectId.GenerateNewId().ToString()
        );

    public static DnsRecord Create(
        string name,
        ResourceType resourceType,
        string resourceId,
        string resourceName,
        string groupId,
        DnsRecordCreateOptions options) =>
        new(
            Id: options.Id,
            Name: name,
            ResourceType: resourceType,
            ResourceId: resourceId,
            ResourceName: resourceName,
            GroupId: groupId
        );
}

public enum ResourceType
{
    WebService,
}
