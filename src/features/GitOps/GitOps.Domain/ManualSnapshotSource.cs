using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record ManualSnapshotSource(
    [property: BsonId] string Id,
    string TenantId,
    string CreationKey,
    string SourceUrl,
    DateTime CreatedAtUtc);
