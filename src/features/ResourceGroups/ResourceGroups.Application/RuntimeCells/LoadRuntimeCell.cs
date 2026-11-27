// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.RuntimeCells;

public delegate Task<RuntimeCell?> LoadRuntimeCell(
    string tenantId,
    string resourceGroupId,
    string runtimeCellId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadRuntimeCell(
    IMongoCollection<RuntimeCell> runtimeCells
)
{
    public async Task<RuntimeCell?> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string runtimeCellId,
        CancellationToken cancellationToken = default) => await runtimeCells
        .Find(RuntimeCellFilters.ByTenantResourceGroupAndId(tenantId, resourceGroupId, runtimeCellId))
        .FirstOrDefaultAsync(cancellationToken);
}
