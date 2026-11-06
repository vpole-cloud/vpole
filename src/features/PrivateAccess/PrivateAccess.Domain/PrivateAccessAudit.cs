using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.PrivateAccess.Domain;

public sealed record PrivateAccessAudit(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string GrantId,
    string? SessionId,
    string ActorUserId,
    DateTime AtUtc,
    PrivateAccessAuditState State) : ITenantOwned;

public static class PrivateAccessAuditFactory
{
    public static PrivateAccessAudit Create(
        string tenantId,
        string grantId,
        string? sessionId,
        string actorUserId,
        DateTime atUtc,
        PrivateAccessAuditState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentNullException.ThrowIfNull(state);
        if (atUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Audit time must be UTC.", nameof(atUtc));
        var requiresSession = state is PrivateAccessSessionIssuedAudit or
            PrivateAccessSessionRedeemedAudit or PrivateAccessSessionDeniedAudit or
            PrivateAccessSessionStartedAudit or PrivateAccessSessionClosedAudit or
            PrivateAccessCleanupClaimedAudit or PrivateAccessCleanupCompletedAudit;
        if (requiresSession == string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Audit session identity does not match its state.", nameof(sessionId));

        return new PrivateAccessAudit(ObjectId.GenerateNewId().ToString(),
            tenantId, grantId, sessionId, actorUserId, atUtc, state);
    }
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(PrivateAccessGrantCreatedAudit),
    typeof(PrivateAccessGrantRevokedAudit),
    typeof(PrivateAccessSessionIssuedAudit),
    typeof(PrivateAccessSessionIssueDeniedAudit),
    typeof(PrivateAccessSessionRedeemedAudit),
    typeof(PrivateAccessSessionDeniedAudit),
    typeof(PrivateAccessSessionStartedAudit),
    typeof(PrivateAccessSessionClosedAudit),
    typeof(PrivateAccessCleanupClaimedAudit),
    typeof(PrivateAccessCleanupCompletedAudit))]
public closed record PrivateAccessAuditState;

[BsonDiscriminator("GrantCreated")]
public sealed record PrivateAccessGrantCreatedAudit : PrivateAccessAuditState;

[BsonDiscriminator("GrantRevoked")]
public sealed record PrivateAccessGrantRevokedAudit : PrivateAccessAuditState;

[BsonDiscriminator("SessionIssued")]
public sealed record PrivateAccessSessionIssuedAudit : PrivateAccessAuditState;

[BsonDiscriminator("SessionIssueDenied")]
public sealed record PrivateAccessSessionIssueDeniedAudit(
    [property: BsonRepresentation(BsonType.String)]
    PrivateAccessAuditReason Reason) : PrivateAccessAuditState;

[BsonDiscriminator("SessionRedeemed")]
public sealed record PrivateAccessSessionRedeemedAudit : PrivateAccessAuditState;

[BsonDiscriminator("SessionDenied")]
public sealed record PrivateAccessSessionDeniedAudit : PrivateAccessAuditState;

[BsonDiscriminator("SessionStarted")]
public sealed record PrivateAccessSessionStartedAudit : PrivateAccessAuditState;

[BsonDiscriminator("SessionClosed")]
public sealed record PrivateAccessSessionClosedAudit : PrivateAccessAuditState;

[BsonDiscriminator("CleanupClaimed")]
public sealed record PrivateAccessCleanupClaimedAudit : PrivateAccessAuditState;

[BsonDiscriminator("CleanupCompleted")]
public sealed record PrivateAccessCleanupCompletedAudit : PrivateAccessAuditState;

public enum PrivateAccessAuditReason
{
    GrantUnavailable,
    SubjectMismatch,
    MembershipRequired,
    DeploymentUnavailable,
    EndpointUnavailable,
    BindingChanged
}
