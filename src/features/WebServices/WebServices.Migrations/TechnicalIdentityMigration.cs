using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.WebServices.Migrations;

public sealed class TechnicalIdentityMigration : IMigration
{
    public ObjectId Id => new("6b700088b213917a38c38002");
    public string Name => "Tenant-wide WebServices technical identity";
    public string Description => "Names identify one kind in a tenant independently of group and source.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => context
        .GetCollection<BsonDocument>("WebServices")
        .Indexes
        .CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Name"),
                new CreateIndexOptions
                {
                    Name = "UX_WebServices_Tenant_TechnicalName",
                    Unique = true,
                    Collation = new Collation("en", strength: CollationStrength.Secondary),
                }),
            cancellationToken: cancellationToken);
}
