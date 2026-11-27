// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Common;

public sealed record IsolationScopeDto(
    string Id,
    string ResourceGroupId,
    string Name,
    bool IsDefault,
    string Lifecycle,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    GitResourceStatus? GitOps = null
);

internal static class IsolationScopeExtensions
{
    public static IsolationScopeDto ToDto(this IsolationScope scope) => new(
        scope.Id,
        scope.ResourceGroupId,
        scope.Name,
        scope.IsDefault,
        scope.Lifecycle switch
        {
            ActiveIsolationScopeLifecycleState => "active",
            DeletingIsolationScopeLifecycleState => "deleting",
            _ => throw new InvalidOperationException(
                $"Unsupported isolation scope lifecycle '{scope.Lifecycle.GetType().Name}'."),
        },
        scope.Revision,
        scope.CreatedAtUtc,
        scope.UpdatedAtUtc);
}
