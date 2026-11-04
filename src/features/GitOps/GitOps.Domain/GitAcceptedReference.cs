using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record GitAcceptedReference(
    [property: BsonId] string Id,
    GitSourceIdentity Source,
    string ConsumerResourceId,
    GitResourceIdentity Target);
