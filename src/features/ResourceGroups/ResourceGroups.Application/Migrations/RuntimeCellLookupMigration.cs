using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.ResourceGroups.Application.Migrations;

public sealed class RuntimeCellLookupMigration : IMigration
{
    public ObjectId Id => new("69d70001b213917a38c379f6");

    public string Name => "Runtime cell tenant batch lookup index";

    public string Description =>
        "Creates the tenant-scoped runtime-cell lookup index used by deployment batch reads.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) =>
        context
            .GetCollection<BsonDocument>("RuntimeCells")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("_id"),
                    new CreateIndexOptions
                    {
                        Name = "IX_RuntimeCells_Tenant_Id"
                    }),
                cancellationToken: cancellationToken);
}
