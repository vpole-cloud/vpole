using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Application.Persistence;

public sealed record ManagedDeclaration(
    [property: BsonId] string Id,
    string TenantId,
    string Kind,
    string Name,
    string SourceId,
    string SourceUrl,
    string ResourceId,
    string DesiredHash,
    string? AppliedHash,
    string? Blocker,
    bool PendingRemoval,
    string LastRunId,
    string LastPhase,
    long MutationFence);
