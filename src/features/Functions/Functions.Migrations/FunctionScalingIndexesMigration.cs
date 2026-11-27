// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Functions.Migrations;

public sealed class FunctionScalingIndexesMigration : IMigration
{
    public ObjectId Id => new("69d50000b213917a38c379f1");
    public string Name => "Function scaling indexes";
    public string Description => "Creates the revision lifecycle index used by the Function scaler.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => context
        .GetCollection<BsonDocument>("FunctionRevisions")
        .Indexes
        .CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("Status._t")
                    .Ascending("TenantId")
                    .Ascending("FunctionAppId"),
                new CreateIndexOptions { Name = "IX_FunctionRevisions_Status_Tenant_App" }),
            cancellationToken: cancellationToken);
}
