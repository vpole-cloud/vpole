// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.FunctionApps;

public delegate Task<FunctionApp?> LoadFunctionAppById(
    string tenantId,
    string functionAppId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadFunctionAppById(
    IMongoCollection<FunctionApp> functionApps
)
{
    public async Task<FunctionApp?> InvokeAsync(
        string tenantId,
        string functionAppId,
        CancellationToken cancellationToken = default) => await functionApps
        .Find(FunctionAppFilters.ByTenantAndId(tenantId, functionAppId))
        .FirstOrDefaultAsync(cancellationToken);
}
