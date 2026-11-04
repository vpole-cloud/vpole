using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.ResourceConnections.Application.Migrations;

public sealed class ResourceConnectionIndexesMigration : IMigration
{
    public const string TenantNameIndex = "UX_ResourceConnections_Tenant_Name";

    public ObjectId Id => new("6a700046b213917a38c38046");
    public string Name => "Resource connection identity";
    public string Description => "Makes connection names unique within each tenant.";

    public async Task Up(
        IMongoDatabase context,
        CancellationToken cancellationToken = default) =>
        await context.GetCollection<BsonDocument>("ResourceConnections").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("Name"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = TenantNameIndex
                }),
            cancellationToken: cancellationToken);
}
