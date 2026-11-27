// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record ResourceGroupOwnershipNotFound(
    string ResourceGroupId
);

public sealed record ResourceGroupOwnershipDeleting(
    string ResourceGroupId
);

public union ResourceGroupOwnershipError(ResourceGroupOwnershipNotFound, ResourceGroupOwnershipDeleting);

public union GetResourceGroupOwnershipOutcome(ResourceGroupOwnership, ResourceGroupOwnershipError);

public delegate Task<GetResourceGroupOwnershipOutcome> GetResourceGroupOwnership(
    string tenantId,
    string resourceGroupId,
    CancellationToken cancellationToken = default);

public sealed record ResourceGroupOwnership(
    string ResourceGroupId,
    string TenantId,
    string DefaultIsolationScopeId,
    ResourceGroupOwnershipLifecycle Lifecycle
);

public enum ResourceGroupOwnershipLifecycle
{
    Active,
    Deleting,
}
