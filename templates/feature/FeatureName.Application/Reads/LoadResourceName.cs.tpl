// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.FeatureName.Application.Mongo;
using Pole.Features.FeatureName.Domain;

namespace Pole.Features.FeatureName.Application.Reads;

public sealed record ResourceNameIdentity(
    string TenantId,
    string Id
);

public delegate Task<ResourceName?> LoadResourceName(
    ResourceNameIdentity identity,
    CancellationToken cancellationToken);

internal sealed class MongoLoadResourceName(
    IMongoCollection<ResourceName> resources
)
{
    public async Task<ResourceName?> InvokeAsync(ResourceNameIdentity identity, CancellationToken ct) =>
        await resources.Find(ResourceNameFilters.ById(identity.TenantId, identity.Id)).FirstOrDefaultAsync(ct);
}
