using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.SeaweedFS.Domain;

public sealed record SeaweedFSBucket(
    [property: BsonId, BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string SeaweedFSId,
    string Name,
    string SecretId,
    long Revision,
    SeaweedFSLifecycle Lifecycle,
    bool ReservesName
) : ITenantOwned;
