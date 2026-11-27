// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Invocations;

public delegate Task<IReadOnlyList<FunctionInvocation>> LoadFunctionInvocations(
    FunctionApp functionApp,
    int take,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadFunctionInvocations(
    IMongoCollection<FunctionInvocation> functionInvocations
)
{
    public async Task<IReadOnlyList<FunctionInvocation>> InvokeAsync(
        FunctionApp functionApp,
        int take,
        CancellationToken cancellationToken = default) => await functionInvocations
        .Find(FunctionInvocationFilters.ByFunctionApp(functionApp.TenantId, functionApp.Id))
        .SortByDescending(invocation => invocation.CreatedAtUtc)
        .Limit(take)
        .ToListAsync(cancellationToken);
}
