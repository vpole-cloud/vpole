// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Mongo;
using Pole.Features.StorageProfiles.Domain;

namespace Pole.Features.StorageProfiles.Application.Catalog;

public delegate Task<IReadOnlyCollection<StorageProfile>> LoadStorageProfiles(
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadStorageProfiles(
    IMongoCollection<StorageProfile> storageProfiles
)
{
    public async Task<IReadOnlyCollection<StorageProfile>> InvokeAsync(CancellationToken cancellationToken = default) =>
        await storageProfiles.Find(StorageProfileFilters.All())
            .SortByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.DisplayName)
            .ThenBy(profile => profile.Id)
            .ToListAsync(cancellationToken);
}
