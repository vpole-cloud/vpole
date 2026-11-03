using Pole.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.RedisServices.Migrations;

public sealed class RedisNameIndexesMigration : IMigration
{
    public const string TenantNameIndex = "UX_RedisServices_Tenant_Name";

    public ObjectId Id => new("6a700002b213917a38c38001");
    public string Name => "Redis tenant name uniqueness";
    public string Description =>
        "Enforces unique Redis service names within each tenant.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) =>
        context.GetCollection<BsonDocument>("RedisServices").Indexes.CreateOneAsync(
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
