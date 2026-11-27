// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Outbox.Migrations;

public sealed class OutboxMessageMigration : IMigration
{
    public const string PendingDueIndexName = "IX_OutboxMessages_Pending_Due";
    public const string PendingKeyIndexName = "UX_OutboxMessages_PendingKey";
    public const string PublishedCleanupIndexName = "IX_OutboxMessages_Published_Cleanup";
    public const string AbandonedCleanupIndexName = "IX_OutboxMessages_Abandoned_Cleanup";

    public ObjectId Id => new("69d80000b213917a38c379f7");
    public string Name => "Outbox message delivery lifecycle";

    public string Description =>
        "Creates due-delivery, pending-key uniqueness, and terminal cleanup indexes for outbox messages.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var outboxMessages = context.GetCollection<BsonDocument>("OutboxMessages");
        await outboxMessages.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("State._t")
                        .Ascending("State.NextAttemptAtUtc")
                        .Ascending("EnqueuedAtUtc")
                        .Ascending("_id"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = PendingDueIndexName,
                        PartialFilterExpression = new BsonDocument("State._t", "pending"),
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("PendingKey"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = PendingKeyIndexName,
                        Unique = true,
                        PartialFilterExpression = new BsonDocument("PendingKey", new BsonDocument("$type", "string")),
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("State._t")
                        .Ascending("State.PublishedAtUtc")
                        .Ascending("_id"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = PublishedCleanupIndexName,
                        PartialFilterExpression = new BsonDocument("State._t", "published"),
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("State._t")
                        .Ascending("State.AbandonedAtUtc")
                        .Ascending("_id"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = AbandonedCleanupIndexName,
                        PartialFilterExpression = new BsonDocument("State._t", "abandoned"),
                    }),
            ],
            cancellationToken);
    }
}
