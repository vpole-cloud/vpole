namespace Pole.Features.PrivateAccess.Contracts;

public sealed record PrivateAccessBrokerOwnerLease(
    PrivateAccessGatewayIdentity Gateway,
    string InstanceId,
    string InternalOrigin,
    long Epoch,
    DateTime ExpiresAtUtc);

public sealed record PrivateAccessBrokerOwnerBusy;
public sealed record PrivateAccessBrokerGatewayUnavailable;

public union ClaimPrivateAccessBrokerOwnerOutcome(
    PrivateAccessBrokerOwnerLease,
    PrivateAccessBrokerOwnerBusy,
    PrivateAccessBrokerGatewayUnavailable);

public delegate Task<ClaimPrivateAccessBrokerOwnerOutcome> ClaimPrivateAccessBrokerOwner(
    PrivateAccessGatewayIdentity gateway,
    string instanceId,
    string internalOrigin,
    CancellationToken cancellationToken = default);

public delegate Task<bool> RenewPrivateAccessBrokerOwner(
    PrivateAccessBrokerOwnerLease lease,
    CancellationToken cancellationToken = default);

public delegate Task ReleasePrivateAccessBrokerOwner(
    PrivateAccessBrokerOwnerLease lease,
    CancellationToken cancellationToken = default);

public delegate Task<PrivateAccessBrokerOwnerLease?> LoadPrivateAccessBrokerOwner(
    string tenantId,
    string deploymentTargetId,
    CancellationToken cancellationToken = default);
