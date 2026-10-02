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

public static class PrivateAccessAuditFactory
{
    public static PrivateAccessAudit Create(
        string tenantId,
        string grantId,
        string? sessionId,
        string actorUserId,
        PrivateAccessAuditAction action,
        DateTime atUtc,
        PrivateAccessAuditReason? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (atUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Audit time must be UTC.", nameof(atUtc));

        return new PrivateAccessAudit(ObjectId.GenerateNewId().ToString(),
            tenantId, grantId, sessionId, actorUserId, action, atUtc, reason);
    }
}

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
