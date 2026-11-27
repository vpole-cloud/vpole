// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.GitProviders.Migrations;

public sealed class GitProviderConnectionsMigration : IMigration
{
    public ObjectId Id => new("69d10000b213917a38c379ec");
    public string Name => "Git provider connections and source repositories";
    public string Description => "Creates tenant, installation, source-link, and setup-expiry indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var connections = context.GetCollection<BsonDocument>("GitProviderConnections");
        await connections.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("Provider").Ascending("ExternalInstallationId"),
                    new CreateIndexOptions
                    {
                        Unique = true,
                        Name = "UX_GitProviderConnections_Provider_Installation",
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("Provider")
                        .Ascending("AccountLogin"),
                    new CreateIndexOptions { Name = "IX_GitProviderConnections_Tenant_Provider_Account" }),
            ],
            cancellationToken);

        await connections.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("TechnicalName"),
                new CreateIndexOptions { Unique = true, Name = "UX_GitProviderConnections_Tenant_TechnicalName" }),
            cancellationToken: cancellationToken);

        var sourceRepositories = context.GetCollection<BsonDocument>("SourceRepositories");
        await sourceRepositories.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("ConnectionId")
                        .Ascending("ExternalRepositoryId")
                        .Ascending("Branch"),
                    new CreateIndexOptions
                    {
                        Unique = true,
                        Name = "UX_SourceRepositories_Tenant_Repository_Branch",
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("RepositoryFullName"),
                    new CreateIndexOptions { Name = "IX_SourceRepositories_Tenant_Name" }),
            ],
            cancellationToken);

        await sourceRepositories.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("TechnicalName"),
                new CreateIndexOptions { Unique = true, Name = "UX_SourceRepositories_Tenant_TechnicalName" }),
            cancellationToken: cancellationToken);

        var setupAttempts = context.GetCollection<BsonDocument>("GitHubSetupAttempts");
        await setupAttempts.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("InstallStateHash"),
                    new CreateIndexOptions
                    {
                        Unique = true,
                        Name = "UX_GitHubSetupAttempts_InstallState",
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("OAuthStateHash"),
                    new CreateIndexOptions
                    {
                        Unique = true,
                        Sparse = true,
                        Name = "UX_GitHubSetupAttempts_OAuthState",
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                    new CreateIndexOptions
                    {
                        ExpireAfter = TimeSpan.Zero,
                        Name = "TTL_GitHubSetupAttempts_ExpiresAtUtc",
                    }),
            ],
            cancellationToken);
    }
}
