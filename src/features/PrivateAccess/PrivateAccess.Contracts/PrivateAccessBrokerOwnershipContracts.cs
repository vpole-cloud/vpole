// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.PrivateAccess.Contracts;

public sealed record PrivateAccessBrokerOwnerLease(
    PrivateAccessGatewayIdentity Gateway,
    string InstanceId,
    string InternalOrigin,
    long Epoch,
    DateTime ExpiresAtUtc
);

public sealed record PrivateAccessBrokerOwnerBusy;

public sealed record PrivateAccessBrokerGatewayUnavailable;

public union ClaimPrivateAccessBrokerOwnerOutcome(
    PrivateAccessBrokerOwnerLease,
    PrivateAccessBrokerOwnerBusy,
    PrivateAccessBrokerGatewayUnavailable);

public delegate Task<ClaimPrivateAccessBrokerOwnerOutcome> ClaimPrivateAccessBrokerOwner(
    ClaimPrivateAccessBrokerOwnerRequest request,
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

public sealed record ClaimPrivateAccessBrokerOwnerRequest(
    PrivateAccessGatewayIdentity Gateway,
    string InstanceId,
    string InternalOrigin
);
