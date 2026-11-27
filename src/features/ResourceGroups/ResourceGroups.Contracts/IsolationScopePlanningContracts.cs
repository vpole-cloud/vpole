// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record NamedIsolationScopeRequest(
    string TenantId,
    string ResourceGroupId,
    string? Name
);

public sealed record IsolationScopePlanningRequest(
    string TenantId,
    string ResourceGroupId,
    string? IsolationScopeId
);

public sealed record IsolationScopePlanningSnapshot(
    string TenantId,
    string ResourceGroupId,
    long ResourceGroupRevision,
    string IsolationScopeId,
    long ScopeRevision,
    string Name,
    bool IsDefault
);

public sealed record IsolationScopePlanningUnavailable;

public sealed record IsolationScopePlanningFenced;

public sealed record IsolationScopePlanningFenceRejected;

public union ResolveNamedIsolationScopeOutcome(IsolationScopePlanningSnapshot, IsolationScopePlanningUnavailable);

public union LoadIsolationScopePlanningSnapshotOutcome(
    IsolationScopePlanningSnapshot,
    IsolationScopePlanningUnavailable);

public union FenceIsolationScopePlanningSnapshotOutcome(
    IsolationScopePlanningFenced,
    IsolationScopePlanningFenceRejected);

public delegate Task<ResolveNamedIsolationScopeOutcome> ResolveNamedIsolationScope(
    NamedIsolationScopeRequest request,
    CancellationToken cancellationToken);

public delegate Task<LoadIsolationScopePlanningSnapshotOutcome> LoadIsolationScopePlanningSnapshot(
    IsolationScopePlanningRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public delegate Task<FenceIsolationScopePlanningSnapshotOutcome> FenceIsolationScopePlanningSnapshot(
    IsolationScopePlanningSnapshot request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
