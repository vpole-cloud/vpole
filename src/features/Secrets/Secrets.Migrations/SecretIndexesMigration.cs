// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Secrets.Migrations;

public sealed class SecretIndexesMigration : IMigration
{
    public ObjectId Id => new("6a700041b213917a38c38041");

    public string Name => "Secret metadata and immutable version indexes";

    public string Description => "Tenant-scoped inventory and immutable version identity.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        // BSON field names define the current persisted schema independently of live domain types.
        await context.GetCollection<BsonDocument>("Secrets")
            .Indexes
            .CreateManyAsync(
                [
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("NormalizedName"),
                        new CreateIndexOptions
                        {
                            Unique = true,
                            Name = "UX_Secrets_Tenant_NormalizedName",
                        }),
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("_id")),
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys
                            .Ascending("TenantId")
                            .Ascending("ResourceGroupId")
                            .Ascending("IsolationScopeId")),
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys
                            .Ascending("TenantId")
                            .Ascending("Bindings.Consumer.ResourceType")
                            .Ascending("Bindings.Consumer.ResourceId")),
                ],
                cancellationToken);

        await context.GetCollection<BsonDocument>("SecretVersions")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SecretId").Ascending("Version"),
                    new CreateIndexOptions
                    {
                        Unique = true,
                    }),
                cancellationToken: cancellationToken);
    }
}
