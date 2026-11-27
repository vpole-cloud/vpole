// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.ResourceDeployments.Migrations;

public sealed class ResourceDeploymentRecoveryIndexMigration : IMigration
{
    public ObjectId Id => new("6a900006b213917a38c38106");

    public string Name => "Resource deployment recovery scan";

    public string Description =>
        "Indexes state and update time used to recover stale pending and waiting resource deployments.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var deployments = context.GetCollection<BsonDocument>("ResourceDeployments");
        return deployments.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("State._t").Ascending("UpdatedAtUtc").Ascending("_id"),
                new CreateIndexOptions
                {
                    Name = "IX_ResourceDeployments_State_Updated",
                }),
            cancellationToken: cancellationToken);
    }
}
