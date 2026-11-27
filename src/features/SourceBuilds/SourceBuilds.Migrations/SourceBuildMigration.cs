// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.SourceBuilds.Migrations;

public sealed class SourceBuildMigration : IMigration
{
    public const string TenantWebServiceIndex = "UX_SourceBuildConfigurations_Tenant_WebService";
    public const string TenantNameIndex = "UX_SourceBuildConfigurations_Tenant_TechnicalName";

    public ObjectId Id => new("69d30000b213917a38c379ee");
    public string Name => "Source build configurations and jobs";
    public string Description => "Creates tenant configuration and build history indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var configurations = context.GetCollection<BsonDocument>("SourceBuildConfigurations");
        await configurations.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("TechnicalName"),
                    new CreateIndexOptions { Unique = true, Name = TenantNameIndex }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("WebServiceId"),
                    new CreateIndexOptions
                    {
                        Unique = true,
                        Name = TenantWebServiceIndex,
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SourceRepositoryId"),
                    new CreateIndexOptions { Name = "IX_SourceBuildConfigurations_Tenant_SourceRepository" }),
            ],
            cancellationToken);

        var jobs = context.GetCollection<BsonDocument>("SourceBuildJobs");
        await jobs.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("ConfigurationId")
                        .Descending("CreatedAtUtc"),
                    new CreateIndexOptions { Name = "IX_SourceBuildJobs_Tenant_Configuration_CreatedAt" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("WebServiceId")
                        .Descending("CreatedAtUtc"),
                    new CreateIndexOptions { Name = "IX_SourceBuildJobs_Tenant_WebService_CreatedAt" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("State._t").Ascending("UpdatedAtUtc"),
                    new CreateIndexOptions { Name = "IX_SourceBuildJobs_State_UpdatedAt" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("State._t")
                        .Ascending("State.ReleaseId"),
                    new CreateIndexOptions { Name = "IX_SourceBuildJobs_Tenant_State_Release" }),
            ],
            cancellationToken);
    }
}
