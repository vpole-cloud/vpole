// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.IsolationScopes;

public delegate Task<IsolationScope?> LoadIsolationScope(
    string tenantId,
    string resourceGroupId,
    string isolationScopeId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadIsolationScope(
    IMongoCollection<IsolationScope> isolationScopes
)
{
    public async Task<IsolationScope?> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        CancellationToken cancellationToken = default) => await isolationScopes
        .Find(IsolationScopeFilters.ByTenantResourceGroupAndId(tenantId, resourceGroupId, isolationScopeId))
        .FirstOrDefaultAsync(cancellationToken);
}
