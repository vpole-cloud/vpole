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
    PrivateAccessSessionState State,
    DateTime? CleanupLastAttemptAtUtc);

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

public static class PrivateAccessSessionFactory
{
    public static PrivateAccessSession Create(
        string tenantId,
        string subjectUserId,
        PrivateAccessSessionGrantSnapshot grant,
        PrivateAccessSessionTicket ticket,
        PrivateAccessSessionRouteSnapshot route,
        DateTime sessionDeadlineUtc,
        DateTime createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectUserId);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(route);
        if (createdAtUtc.Kind != DateTimeKind.Utc ||
            sessionDeadlineUtc.Kind != DateTimeKind.Utc ||
            ticket.ExpiresAtUtc.Kind != DateTimeKind.Utc ||
            createdAtUtc >= ticket.ExpiresAtUtc ||
            ticket.ExpiresAtUtc > sessionDeadlineUtc)
            throw new ArgumentException("Private access session times must form a valid UTC window.");
        if (grant.Revision < 1 || route.DeploymentRevision < 1 || route.DesiredGeneration < 1)
            throw new ArgumentException("Private access session revisions must be positive.");
        if (route.ServicePort is < 1 or > 65535 || route.TargetPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(route), "Private access route ports must be valid TCP ports.");

        return new PrivateAccessSession(
            ObjectId.GenerateNewId().ToString(),
            tenantId,
            subjectUserId,
            grant,
            ticket,
            route,
            sessionDeadlineUtc,
            createdAtUtc,
            1,
            new PrivateAccessTicketPending(),
            null);
    }
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
