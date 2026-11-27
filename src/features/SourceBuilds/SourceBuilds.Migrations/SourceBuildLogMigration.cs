// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.SourceBuilds.Migrations;

public sealed class SourceBuildLogMigration : IMigration
{
    public ObjectId Id => new("69d40000b213917a38c379ee");
    public string Name => "Bounded source build logs";
    public string Description => "Indexes retained log captures for expiry and interrupted worker cleanup.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => context
        .GetCollection<BsonDocument>("SourceBuildLogs")
        .Indexes
        .CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                    new CreateIndexOptions { Name = "IX_SourceBuildLogs_Expiry" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("State._t").Ascending("State.LeaseExpiresAtUtc"),
                    new CreateIndexOptions { Name = "IX_SourceBuildLogs_Lease" }),
            ],
            cancellationToken);
}
