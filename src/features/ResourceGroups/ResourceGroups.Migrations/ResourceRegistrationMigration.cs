// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.ResourceGroups.Migrations;

public sealed class ResourceRegistrationMigration : IMigration
{
    public ObjectId Id => new("69db0000b213917a38c379f6");
    public string Name => "Resource registrations";

    public string Description =>
        "Creates stable resource identity and resource-group listing indexes for resource registrations.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var registrations = context.GetCollection<BsonDocument>("ResourceRegistrations");
        await registrations.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("ResourceTypeId")
                        .Ascending("ResourceId"),
                    new CreateIndexOptions
                    {
                        Unique = true,
                        Name = "UX_ResourceRegistrations_Tenant_Type_Resource",
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("ResourceGroupId")
                        .Ascending("ResourceTypeId")
                        .Ascending("ResourceId"),
                    new CreateIndexOptions
                    {
                        Name = "IX_ResourceRegistrations_Tenant_Group_Type_Resource",
                    }),
            ],
            cancellationToken);
    }
}
