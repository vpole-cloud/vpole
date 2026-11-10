using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.GitOps.Domain;

public sealed record GitSourceRequestReceipt(
    [property: BsonId] string Id,
    string TenantId,
    string SourceId,
    string RequestKey,
    string InputHash,
    long Generation,
    GitRunInitiator Initiator,
    DateTime RequestedAtUtc
) : ITenantOwned;
