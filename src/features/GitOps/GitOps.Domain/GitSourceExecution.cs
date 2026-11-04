using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitOps.Domain;

public sealed record GitSourceExecution(
    [property: BsonId] string Id,
    string TenantId,
    string SourceId,
    long Fence,
    long MutationFence,
    string RunId,
    string ObservedCommit,
    string AttemptedCommit,
    string? AppliedCommit,
    SnapshotProgress Status,
    ImmutableArray<string> DesiredAddresses,
    string SnapshotJson,
    string SourceUrl,
    string ActorUserId,
    string ActorEmail,
    string ApiVersion,
    ImmutableArray<string> ReferencedSecrets,
    long Attempt,
    DateTime? NextAttemptAtUtc);
