// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.ResourceDeployments.Migrations;

internal static class ObservationStoreIndexes
{
    public static async Task CreateAsync(IMongoDatabase database, CancellationToken ct = default)
    {
        await database.GetCollection<BsonDocument>("ResourceDeploymentObservationDemands")
            .Indexes
            .CreateManyAsync(
                [
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys
                            .Ascending("TenantId")
                            .Ascending("DeploymentId")
                            .Ascending("ExpiresAtUtc"),
                        new CreateIndexOptions { Name = "IX_ObservationDemands_Active" }),
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                        new CreateIndexOptions { Name = "TTL_ObservationDemands", ExpireAfter = TimeSpan.Zero }),
                ],
                ct);
        await database.GetCollection<BsonDocument>("ResourceDeploymentObservationControls")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("IdleSinceUtc").Ascending("UpdatedAtUtc"),
                    new CreateIndexOptions { Name = "IX_ObservationControls_Reaping" }),
                cancellationToken: ct);
        await database.GetCollection<BsonDocument>("ResourceDeploymentObservationSnapshots")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                    new CreateIndexOptions { Name = "TTL_ObservationSnapshots", ExpireAfter = TimeSpan.Zero }),
                cancellationToken: ct);
    }
}
