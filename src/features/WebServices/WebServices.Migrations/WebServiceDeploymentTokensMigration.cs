// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.WebServices.Migrations;

public sealed class WebServiceDeploymentTokensMigration : IMigration
{
    public ObjectId Id => new("69d00000b213917a38c379eb");
    public string Name => "Web service deployment tokens";
    public string Description => "Creates indexes for service-scoped CI deployment tokens.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var tokens = context.GetCollection<BsonDocument>("WebServiceDeploymentTokens");

        await tokens.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TokenHash"),
                    new CreateIndexOptions
                    {
                        Unique = true,
                        Name = "IX_WebServiceDeploymentTokens_TokenHash",
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("WebServiceId")
                        .Descending("CreatedAtUtc"),
                    new CreateIndexOptions
                    {
                        Name = "IX_WebServiceDeploymentTokens_Tenant_Service_CreatedAt",
                    }),
            ],
            cancellationToken);
    }
}
