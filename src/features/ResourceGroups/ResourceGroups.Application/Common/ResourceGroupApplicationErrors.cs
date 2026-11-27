// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ResourceGroups.Application.Common;

public sealed record ResourceGroupNotFound(
    string ResourceGroupId
);

public sealed record ResourceGroupDeleting(
    string ResourceGroupId
);

public enum ResourceGroupRevisionConflictReason { ExpectedRevisionConflict, GitOwnedConfiguration }

public sealed record ResourceGroupRevisionConflict(
    string ResourceGroupId,
    ResourceGroupRevisionConflictReason Reason
);

public sealed record ResourceGroupContainsResources(
    string ResourceGroupId
);

public sealed record IsolationScopeNotFound(
    string IsolationScopeId
);

public sealed record IsolationScopeNameConflict(
    string ResourceGroupId,
    string NormalizedName
);

public sealed record IsolationScopeDeleting(
    string IsolationScopeId
);

public sealed record DefaultIsolationScopeDeletionForbidden(
    string IsolationScopeId
);

public sealed record RuntimeCellNotFound(
    string RuntimeCellId
);

public sealed record RuntimeCellRetryRejected(
    string RuntimeCellId
);

public sealed record ResourceGroupDeploymentInProgress(
    string ResourceGroupId
);

public sealed record IsolationScopeDeploymentInProgress(
    string IsolationScopeId
);

public union ResourceGroupMutationError(
    ResourceGroupNotFound,
    ResourceGroupDeleting,
    ResourceGroupRevisionConflict,
    ResourceGroupContainsResources,
    IsolationScopeNotFound,
    IsolationScopeNameConflict,
    IsolationScopeDeleting,
    DefaultIsolationScopeDeletionForbidden,
    RuntimeCellNotFound,
    RuntimeCellRetryRejected,
    ResourceGroupDeploymentInProgress,
    IsolationScopeDeploymentInProgress);
