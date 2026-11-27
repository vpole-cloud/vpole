// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.RuntimeCells;

public delegate Task<IReadOnlyList<RuntimeCell>> LoadRuntimeCells(
    string tenantId,
    string resourceGroupId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadRuntimeCells(
    IMongoCollection<RuntimeCell> runtimeCells
)
{
    public async Task<IReadOnlyList<RuntimeCell>> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default) => await runtimeCells
        .Find(RuntimeCellFilters.ByTenantAndResourceGroup(tenantId, resourceGroupId))
        .SortBy(cell => cell.IsolationScopeId)
        .ThenBy(cell => cell.DeploymentTargetId)
        .ToListAsync(cancellationToken);
}
