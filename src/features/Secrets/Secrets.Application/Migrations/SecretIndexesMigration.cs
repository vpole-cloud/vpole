using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.Secrets.Application.Migrations;

public sealed class SecretIndexesMigration : IMigration
{
    public ObjectId Id => new("6a700041b213917a38c38041");

    public string Name => "Secret metadata and immutable version indexes";

    public string Description => "Tenant-scoped inventory and immutable version identity.";

    public async Task Up(
        IMongoDatabase context,
        CancellationToken cancellationToken = default)
    {
        // Index field names are frozen historical BSON contracts, independent of live domain types.
        await context.GetCollection<BsonDocument>("Secrets").Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("ResourceGroupId")
                    .Ascending("IsolationScopeId")
                    .Ascending("NormalizedName"),
                new CreateIndexOptions
                {
                    Unique = true
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("_id")),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("ResourceGroupId")
                    .Ascending("IsolationScopeId")),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("Bindings.ConsumerType")
                    .Ascending("Bindings.ConsumerId"))
        ], cancellationToken);

        await context.GetCollection<BsonDocument>("SecretVersions").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("SecretId")
                    .Ascending("Version"),
                new CreateIndexOptions
                {
                    Unique = true
                }),
            cancellationToken: cancellationToken);
    }
}
