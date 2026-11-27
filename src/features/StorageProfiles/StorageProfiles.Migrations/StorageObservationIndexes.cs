// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.StorageProfiles.Migrations;

internal static class StorageObservationIndexes
{
    internal static async Task CreateAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        await database.GetCollection<BsonDocument>("StorageObservationDemands")
            .Indexes
            .CreateManyAsync(
                [
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("DeploymentTargetId").Ascending("ExpiresAtUtc"),
                        new CreateIndexOptions { Name = "IX_StorageObservationDemands_Active" }),
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                        new CreateIndexOptions { Name = "TTL_StorageObservationDemands", ExpireAfter = TimeSpan.Zero }),
                ],
                ct);
        await database.GetCollection<BsonDocument>("StorageObservationControls")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("IdleSinceUtc").Ascending("UpdatedAtUtc"),
                    new CreateIndexOptions { Name = "IX_StorageObservationControls_Reaping" }),
                cancellationToken: ct);
        await database.GetCollection<BsonDocument>("StorageObservationSnapshots")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                    new CreateIndexOptions { Name = "TTL_StorageObservationSnapshots", ExpireAfter = TimeSpan.Zero }),
                cancellationToken: ct);
    }
}
