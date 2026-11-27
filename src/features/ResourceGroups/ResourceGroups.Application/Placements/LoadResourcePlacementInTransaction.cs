// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Placements;

internal sealed class MongoLoadResourcePlacementInTransaction(
    IMongoCollection<RuntimeCell> runtimeCells
)
{
    public async Task<ResourcePlacement?> InvokeAsync(
        LoadResourcePlacementInTransactionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var runtimeCell = await runtimeCells
            .Find(session, RuntimeCellFilters.ByTenantAndId(request.TenantId, request.RuntimeCellId))
            .FirstOrDefaultAsync(cancellationToken);
        return runtimeCell?.ToPlacement();
    }
}
