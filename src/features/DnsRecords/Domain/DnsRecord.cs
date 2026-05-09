using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.Shared.Models;

namespace Pole.Feature.DnsRecords.Domain;

public record DnsRecord(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string Domain,
    ResourceInfo Resource
);

public sealed record ResourceInfo(
    string Id,
    string Name,
    ResourceType ResourceType,
    string GroupId
);

public static class DnsRecordFactory
{
    public sealed record DnsRecordCreateOptions(
        string? Id
    );

    public static DnsRecordCreateOptions DefaultCreateOptions =>
        new(
            Id: null
        );

    public static DnsRecord Create(
        string name,
        ResourceType resourceType,
        string resourceId,
        string resourceName,
        string groupId,
        DnsRecordCreateOptions options) =>
        new(
            Id: options.Id ?? ObjectId.GenerateNewId().ToString(),
            Domain: name,
            Resource: new ResourceInfo(
                resourceId,
                resourceName,
                resourceType,
                groupId)
        );
}