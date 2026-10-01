using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.PrivateAccess.Domain;

public sealed record PrivateAccessSession(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string SubjectUserId,
    string GrantId,
    long GrantRevision,
    string TicketHash,
    DateTime TicketExpiresAtUtc,
    DateTime SessionDeadlineUtc,
    DateTime CreatedAtUtc,
    long Revision,
    PrivateAccessSessionState State,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string EndpointId,
    string DeploymentTargetId,
    string RuntimeCellId,
    string IsolationScopeId,
    string KubernetesNamespace,
    string ServiceName,
    int ServicePort,
    int TargetPort,
    long DeploymentRevision,
    long DesiredGeneration)
{
    public DateTime? CleanupLastAttemptAtUtc { get; init; }
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(PrivateAccessTicketPending),
    typeof(PrivateAccessTicketRedeemed),
    typeof(PrivateAccessSessionActive),
    typeof(PrivateAccessSessionTerminated),
    typeof(PrivateAccessSessionCleaning),
    typeof(PrivateAccessSessionCleaned))]
public closed record PrivateAccessSessionState;

[BsonDiscriminator("TicketPending")]
public sealed record PrivateAccessTicketPending : PrivateAccessSessionState;

[BsonDiscriminator("TicketRedeemed")]
public sealed record PrivateAccessTicketRedeemed(
    DateTime RedeemedAtUtc,
    DateTime? LastAuthorizationExpiresAtUtc) : PrivateAccessSessionState;

[BsonDiscriminator("Active")]
public sealed record PrivateAccessSessionActive(
    DateTime RedeemedAtUtc,
    DateTime StartedAtUtc,
    DateTime LastLeaseExpiresAtUtc) : PrivateAccessSessionState;

[BsonDiscriminator("Terminated")]
public sealed record PrivateAccessSessionTerminated(
    DateTime ClosedAtUtc,
    string Reason) : PrivateAccessSessionState;

[BsonDiscriminator("Cleaning")]
public sealed record PrivateAccessSessionCleaning(
    DateTime ClaimedAtUtc,
    string Reason) : PrivateAccessSessionState;

[BsonDiscriminator("Cleaned")]
public sealed record PrivateAccessSessionCleaned(
    DateTime CleanedAtUtc,
    string Reason) : PrivateAccessSessionState;
