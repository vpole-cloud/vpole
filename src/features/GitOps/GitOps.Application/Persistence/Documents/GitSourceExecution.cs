using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Application.Persistence;

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
    string Status,
    string[] DesiredAddresses,
    string SnapshotJson,
    string SourceUrl,
    string ActorUserId,
    string ActorEmail,
    string ApiVersion,
    string[] ReferencedSecrets,
    long Attempt = 0,
    DateTime? NextAttemptAtUtc = null)
{
    public GitMutationContext ToContext() => new(
        TenantId, SourceId, RunId, Fence, SourceUrl, ActorUserId, ActorEmail, Attempt);
}
