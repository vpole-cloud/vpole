using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.PrivateAccess.Domain;

public sealed record PrivateAccessAudit(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string GrantId,
    string? SessionId,
    string ActorUserId,
    string Action,
    DateTime AtUtc);
