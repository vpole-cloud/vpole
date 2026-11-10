using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.ResourceGroups.Migrations;

public sealed class TechnicalIdentityMigration : IMigration
{
    public ObjectId Id => new("6b700088b213917a38c38001");
    public string Name => "Tenant-wide ResourceGroups technical identity";
    public string Description => "Names identify one kind in a tenant independently of group and source.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => context
        .GetCollection<BsonDocument>("ResourceGroups")
        .Indexes
        .CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Name"),
                new CreateIndexOptions
                {
                    Name = "UX_ResourceGroups_Tenant_TechnicalName",
                    Unique = true,
                    Collation = new Collation("en", strength: CollationStrength.Secondary),
                }),
            cancellationToken: cancellationToken);
}
