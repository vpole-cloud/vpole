// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.FunctionApps;

public sealed record FunctionAppListQuery(
    string TenantId,
    string? Search,
    string? ResourceGroupId,
    int PageNumber,
    int PageSize
);

public sealed record FunctionAppPage(
    IReadOnlyList<FunctionApp> Items,
    long TotalCount
);

public delegate Task<FunctionAppPage> LoadFunctionAppPage(
    FunctionAppListQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadFunctionAppPage(
    IMongoCollection<FunctionApp> functionApps
)
{
    public async Task<FunctionAppPage> InvokeAsync(
        FunctionAppListQuery query,
        CancellationToken cancellationToken = default)
    {
        var filter = FunctionAppFilters.ForList(query);
        var totalCount = await functionApps.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var items = await functionApps.Find(filter)
            .SortByDescending(app => app.CreatedAtUtc)
            .Skip((query.PageNumber - 1) * query.PageSize)
            .Limit(query.PageSize)
            .ToListAsync(cancellationToken);
        return new FunctionAppPage(items, totalCount);
    }
}
