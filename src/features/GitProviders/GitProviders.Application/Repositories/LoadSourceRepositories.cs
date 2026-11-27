// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Mongo;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Repositories;

public delegate Task<IReadOnlyList<SourceRepository>> LoadSourceRepositories(
    string tenantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceRepositories(
    IMongoCollection<SourceRepository> sourceRepositories
)
{
    public async Task<IReadOnlyList<SourceRepository>> InvokeAsync(
        string tenantId,
        CancellationToken cancellationToken = default) => await sourceRepositories
        .Find(GitProviderFilters.SourceRepositoriesByTenant(tenantId))
        .SortBy(item => item.RepositoryFullName)
        .ThenBy(item => item.Branch)
        .ToListAsync(cancellationToken);
}
