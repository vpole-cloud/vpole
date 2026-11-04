using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record GitOperationReceipt(
    [property: BsonId] string Id,
    string TenantId,
    string SourceId,
    string RunId,
    long Fence,
    string Kind,
    string Name,
    string Phase,
    string ResourceId,
    string DesiredHash,
    string? Blocker,
    bool Changed);
