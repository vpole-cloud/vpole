using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.PrivateAccess.Domain;

public sealed record PrivateAccessSession(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string SubjectUserId,
    PrivateAccessSessionGrantSnapshot Grant,
    PrivateAccessSessionTicket Ticket,
    PrivateAccessSessionRouteSnapshot Route,
    DateTime SessionDeadlineUtc,
    DateTime CreatedAtUtc,
    long Revision,
    PrivateAccessSessionState State)
{
    public DateTime? CleanupLastAttemptAtUtc { get; init; }
}

public sealed record PrivateAccessSessionGrantSnapshot(
    string Id,
    long Revision,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string EndpointId);

public sealed record PrivateAccessSessionTicket(
    string Hash,
    DateTime ExpiresAtUtc);

public sealed record PrivateAccessSessionRouteSnapshot(
    string DeploymentTargetId,
    string RuntimeCellId,
    string IsolationScopeId,
    string KubernetesNamespace,
    string ServiceName,
    int ServicePort,
    int TargetPort,
    long DeploymentRevision,
    long DesiredGeneration);

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
