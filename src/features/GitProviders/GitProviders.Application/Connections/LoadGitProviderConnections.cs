// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Mongo;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Connections;

public delegate Task<IReadOnlyList<GitProviderConnection>> LoadGitProviderConnections(
    string tenantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadGitProviderConnections(
    IMongoCollection<GitProviderConnection> gitProviderConnections
)
{
    public async Task<IReadOnlyList<GitProviderConnection>> InvokeAsync(
        string tenantId,
        CancellationToken cancellationToken = default) => await gitProviderConnections
        .Find(GitProviderFilters.ConnectionsByTenant(tenantId))
        .SortBy(item => item.Provider)
        .ThenBy(item => item.AccountLogin)
        .ToListAsync(cancellationToken);
}
