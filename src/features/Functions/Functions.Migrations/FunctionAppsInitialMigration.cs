// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Functions.Migrations;

public sealed class FunctionAppsInitialMigration : IMigration
{
    public ObjectId Id => new("69d50000b213917a38c379f0");
    public string Name => "Function Apps initial indexes";
    public string Description => "Creates tenant, immutable revision, active revision, and global route indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var apps = context.GetCollection<BsonDocument>("FunctionApps");
        var revisions = context.GetCollection<BsonDocument>("FunctionRevisions");
        var routes = context.GetCollection<BsonDocument>("FunctionRouteBindings");

        await apps.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Descending("CreatedAtUtc"),
                    new CreateIndexOptions { Name = "IX_FunctionApps_Tenant_CreatedAt" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("ResourceGroupId"),
                    new CreateIndexOptions { Name = "IX_FunctionApps_Tenant_ResourceGroup" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("NormalizedName"),
                    new CreateIndexOptions
                    {
                        Name = "UX_FunctionApps_Tenant_NormalizedName",
                        Unique = true,
                    }),
            ],
            cancellationToken);

        await revisions.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("FunctionAppId")
                        .Ascending("RevisionNumber"),
                    new CreateIndexOptions
                    {
                        Name = "UX_FunctionRevisions_Tenant_App_Revision",
                        Unique = true,
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("FunctionAppId")
                        .Descending("CreatedAtUtc"),
                    new CreateIndexOptions { Name = "IX_FunctionRevisions_Tenant_App_CreatedAt" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("FunctionAppId"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = "UX_FunctionRevisions_Tenant_App_Active",
                        Unique = true,
                        PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("Status._t", "active"),
                    }),
            ],
            cancellationToken);

        await routes.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("FunctionAppId"),
                new CreateIndexOptions
                {
                    Name = "UX_FunctionRouteBindings_Tenant_App",
                    Unique = true,
                }),
            cancellationToken: cancellationToken);
    }
}
