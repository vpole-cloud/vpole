// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Quotas.Migrations;

public sealed class QuotaMigration : IMigration
{
    public ObjectId Id => new("69d40000b213917a38c379ef");
    public string Name => "Tenant quota policies, usage, and reservations";
    public string Description => "Creates quota admission indexes, including TTL cleanup for reservation history.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var reservations = context.GetCollection<BsonDocument>("QuotaReservations");
        await reservations.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("State.Kind")
                        .Ascending("State.ExpiresAtUtc"),
                    new CreateIndexOptions { Name = "IX_QuotaReservations_Tenant_State_Expires" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("State.CleanupAfterUtc"),
                    new CreateIndexOptions
                    {
                        ExpireAfter = TimeSpan.Zero,
                        Name = "TTL_QuotaReservations_State_CleanupAfterUtc",
                    }),
            ],
            cancellationToken);
    }
}
