// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record RuntimeCellIngressBaselineReady;

public sealed record RuntimeCellIngressBaselineNotReady(
    string RuntimeCellId
);

public sealed record RuntimeCellIngressBaselinePlacementMismatch(
    string RuntimeCellId
);

public sealed record RuntimeCellIngressBaselineNamespaceOwnershipConflict(
    string RuntimeCellId
);

public sealed record RuntimeCellIngressBaselineNamespaceUnavailable(
    string RuntimeCellId
);

public sealed record RuntimeCellIngressBaselinePolicyOwnershipConflict(
    string RuntimeCellId
);

public sealed record RuntimeCellIngressBaselinePolicyUnavailable(
    string RuntimeCellId
);

public sealed record RuntimeCellIngressBaselineTargetNotFound(
    string RuntimeCellId
);

public sealed record RuntimeCellIngressBaselineTargetDisabled(
    string RuntimeCellId
);

public sealed record RuntimeCellIngressBaselineDefaultTargetNotConfigured(
    string RuntimeCellId
);

public sealed record RuntimeCellIngressBaselineTargetConnectionUnavailable(
    string RuntimeCellId
);

public union EnsureRuntimeCellIngressBaselineOutcome(
    RuntimeCellIngressBaselineReady,
    RuntimeCellIngressBaselineNotReady,
    RuntimeCellIngressBaselinePlacementMismatch,
    RuntimeCellIngressBaselineNamespaceOwnershipConflict,
    RuntimeCellIngressBaselineNamespaceUnavailable,
    RuntimeCellIngressBaselinePolicyOwnershipConflict,
    RuntimeCellIngressBaselinePolicyUnavailable,
    RuntimeCellIngressBaselineTargetNotFound,
    RuntimeCellIngressBaselineTargetDisabled,
    RuntimeCellIngressBaselineDefaultTargetNotConfigured,
    RuntimeCellIngressBaselineTargetConnectionUnavailable);

public delegate Task<EnsureRuntimeCellIngressBaselineOutcome> EnsureRuntimeCellIngressBaseline(
    EnsureRuntimeCellIngressBaselineRequest request,
    CancellationToken cancellationToken = default);

public sealed record EnsureRuntimeCellIngressBaselineRequest(
    string TenantId,
    string RuntimeCellId,
    string DeploymentTargetId,
    string KubernetesNamespace
);
