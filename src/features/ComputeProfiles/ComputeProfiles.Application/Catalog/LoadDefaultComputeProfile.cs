// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Mongo;
using Pole.Features.ComputeProfiles.Domain;

namespace Pole.Features.ComputeProfiles.Application.Catalog;

public delegate Task<ComputeProfile?> LoadDefaultComputeProfile(CancellationToken cancellationToken = default);

internal sealed class MongoLoadDefaultComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles
)
{
    public async Task<ComputeProfile?> InvokeAsync(CancellationToken cancellationToken = default)
    {
        var profile = await computeProfiles.Find(ComputeProfileFilters.Default())
            .FirstOrDefaultAsync(cancellationToken);
        return profile;
    }
}
