using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Migrations;

public sealed class RedisNameIndexesMigration : IMigration
{
    public const string TenantNameIndex = "UX_RedisServices_Tenant_Name";

    public ObjectId Id => new("6a700002b213917a38c38001");
    public string Name => "Redis tenant name uniqueness";
    public string Description =>
        "Enforces unique Redis service names within each tenant.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) =>
        context.GetCollection<Redis>("RedisServices").Indexes.CreateOneAsync(
            new CreateIndexModel<Redis>(
                Builders<Redis>.IndexKeys
                    .Ascending(redis => redis.TenantId)
                    .Ascending(redis => redis.Name),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = TenantNameIndex
                }),
            cancellationToken: cancellationToken);
}
