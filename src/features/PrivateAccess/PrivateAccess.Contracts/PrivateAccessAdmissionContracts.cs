using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Contracts;

public sealed record PrivateAccessLeaseSnapshot(
    string TenantId,
    string SubjectUserId,
    string GrantId,
    string SessionId,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string EndpointId,
    string DeploymentTargetId,
    string RuntimeCellId,
    string IsolationScopeId,
    string KubernetesNamespace,
    string ServiceName,
    NetworkPort ServicePort,
    NetworkPort TargetPort,
    long DeploymentRevision,
    DateTime ExpiresAtUtc,
    DateTime SessionDeadlineUtc);

public sealed record PrivateAccessTicketInvalid;
public sealed record PrivateAccessTicketUsed;
public sealed record PrivateAccessSessionUnavailable;
public sealed record PrivateAccessGrantUnavailable;
public sealed record PrivateAccessSubjectMembershipRequired;
public sealed record PrivateAccessDeploymentUnavailable;
public sealed record PrivateAccessEndpointUnavailable;
public sealed record PrivateAccessBindingChanged;

public union RedeemPrivateAccessSessionOutcome(
    PrivateAccessLeaseSnapshot,
    PrivateAccessTicketInvalid,
    PrivateAccessTicketUsed,
    PrivateAccessGrantUnavailable,
    PrivateAccessSubjectMembershipRequired,
    PrivateAccessDeploymentUnavailable,
    PrivateAccessEndpointUnavailable,
    PrivateAccessBindingChanged);

public union RenewPrivateAccessSessionOutcome(
    PrivateAccessLeaseSnapshot,
    PrivateAccessSessionUnavailable,
    PrivateAccessGrantUnavailable,
    PrivateAccessSubjectMembershipRequired,
    PrivateAccessDeploymentUnavailable,
    PrivateAccessEndpointUnavailable,
    PrivateAccessBindingChanged);

public delegate Task<RedeemPrivateAccessSessionOutcome> RedeemPrivateAccessSession(
    string ticket,
    CancellationToken cancellationToken = default);

public delegate Task<RenewPrivateAccessSessionOutcome> RenewPrivateAccessSession(
    string sessionId,
    CancellationToken cancellationToken = default);

public enum PrivateAccessSessionCloseReason
{
    ClientDisconnected,
    SetupFailed,
    BrokerFailure,
    GatewayFailure,
    LeaseDenied,
    Cancelled
}

public sealed record PrivateAccessSessionStarted;
public sealed record PrivateAccessSessionClosed;

public union MarkPrivateAccessSessionStartedOutcome(
    PrivateAccessSessionStarted,
    PrivateAccessSessionUnavailable);

public union ClosePrivateAccessSessionOutcome(
    PrivateAccessSessionClosed,
    PrivateAccessSessionUnavailable);

public delegate Task<MarkPrivateAccessSessionStartedOutcome> MarkPrivateAccessSessionStarted(
    string sessionId,
    CancellationToken cancellationToken = default);

public delegate Task<ClosePrivateAccessSessionOutcome> ClosePrivateAccessSession(
    string sessionId,
    PrivateAccessSessionCloseReason reason,
    CancellationToken cancellationToken = default);

public sealed record PrivateAccessSessionCleanupSnapshot(
    string SessionId,
    string TenantId,
    string GrantId,
    string DeploymentTargetId,
    string RuntimeCellId,
    string IsolationScopeId,
    string ResourceDeploymentId,
    string KubernetesNamespace,
    string ServiceName,
    NetworkPort ServicePort,
    NetworkPort TargetPort);

/// <summary>Atomically claims eligible sessions into Cleaning before returning them.
/// Repeated calls also return previously claimed Cleaning sessions for crash recovery.</summary>
public delegate Task<IReadOnlyList<PrivateAccessSessionCleanupSnapshot>>
    LoadExpiredPrivateAccessSessionCleanupPage(
        DateTime nowUtc,
        int limit,
        CancellationToken cancellationToken = default);

public sealed record PrivateAccessSessionCleaned;
public sealed record PrivateAccessSessionNotCleaning;

public union MarkPrivateAccessSessionCleanedOutcome(
    PrivateAccessSessionCleaned,
    PrivateAccessSessionUnavailable,
    PrivateAccessSessionNotCleaning);

public delegate Task<MarkPrivateAccessSessionCleanedOutcome> MarkPrivateAccessSessionCleaned(
    string sessionId,
    CancellationToken cancellationToken = default);
