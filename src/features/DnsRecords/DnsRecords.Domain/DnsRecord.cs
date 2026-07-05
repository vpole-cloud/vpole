using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.Shared.Models;

namespace Pole.Features.DnsRecords.Domain;

public record DnsRecord(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string Domain,
    ResourceInfo Resource,
    DnsRecordStatus Status
);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(Pending), typeof(Committed))]
public abstract record DnsRecordStatus;

public sealed record Pending(DateTime ReservedAtUtc, DateTime ExpiresAtUtc) : DnsRecordStatus;

public sealed record Committed(DateTime CommittedAtUtc) : DnsRecordStatus;

public sealed record ResourceInfo(
    string Id,
    string Name,
    ResourceType ResourceType,
    string GroupId
);

public static class DnsRecordFactory
{
    public sealed record DnsRecordCreateOptions(
        string? Id,
        DnsRecordStatus? Status
    );

    public static DnsRecordCreateOptions DefaultCreateOptions =>
        new(
            Id: null,
            Status: null
        );

    public static DnsRecord Create(
        string name,
        string tenantId,
        ResourceType resourceType,
        string resourceId,
        string resourceName,
        string groupId,
        DnsRecordCreateOptions options)
    {
        var status = options.Status ?? new Committed(DateTime.UtcNow);

        return new(
            Id: options.Id ?? ObjectId.GenerateNewId().ToString(),
            TenantId: tenantId,
            Domain: name,
            Resource: new ResourceInfo(
                resourceId,
                resourceName,
                resourceType,
                groupId),
            Status: status
        );
    }
}

