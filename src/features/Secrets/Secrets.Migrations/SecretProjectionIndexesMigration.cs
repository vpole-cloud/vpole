// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Secrets.Migrations;

public sealed class SecretProjectionIndexesMigration : IMigration
{
    public ObjectId Id => new("6a700043b213917a38c38043");
    public string Name => "Secret projection and managed owner identity";
    public string Description => "Per-deployment projection lookup and unique live managed owner.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        await context.GetCollection<BsonDocument>("SecretProjections")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SecretId")),
                cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>("Secrets")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("Management.ResourceType")
                        .Ascending("Management.ResourceId"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = "UX_Secrets_ManagedOwner",
                        Unique = true,
                        PartialFilterExpression = new BsonDocument("Management._t", "ProviderManagedSecret"),
                    }),
                cancellationToken: cancellationToken);
    }
}
