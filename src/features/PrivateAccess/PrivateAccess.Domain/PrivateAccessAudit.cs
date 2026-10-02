using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.PrivateAccess.Domain;

public sealed record PrivateAccessAudit(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string GrantId,
    string? SessionId,
    string ActorUserId,
    [property: BsonRepresentation(BsonType.String)]
    PrivateAccessAuditAction Action,
    DateTime AtUtc,
    [property: BsonRepresentation(BsonType.String)]
    PrivateAccessAuditReason? Reason = null);

public enum PrivateAccessAuditAction
{
    GrantCreated,
    GrantRevoked,
    SessionIssued,
    SessionIssueDenied,
    SessionRedeemed,
    SessionDenied,
    SessionStarted,
    SessionClosed,
    CleanupClaimed,
    CleanupCompleted
}

public enum PrivateAccessAuditReason
{
    GrantUnavailable,
    SubjectMismatch,
    MembershipRequired,
    DeploymentUnavailable,
    EndpointUnavailable,
    BindingChanged
}
