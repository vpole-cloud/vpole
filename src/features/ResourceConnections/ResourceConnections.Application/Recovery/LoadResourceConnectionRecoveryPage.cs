// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Features.ResourceConnections.Application.Recovery;

public delegate Task<IReadOnlyList<ResourceConnection>> LoadResourceConnectionRecoveryPage(
    string? afterId,
    int pageSize,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadResourceConnectionRecoveryPage(
    IMongoCollection<ResourceConnection> connections
)
{
    public async Task<IReadOnlyList<ResourceConnection>> InvokeAsync(
        string? afterId,
        int pageSize,
        CancellationToken cancellationToken = default) => await connections
        .Find(ResourceConnectionFilters.AfterId(afterId))
        .SortBy(item => item.Id)
        .Limit(pageSize)
        .ToListAsync(cancellationToken);
}
