using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Secrets.Domain;

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
        await context.GetCollection<Secret>("Secrets").Indexes.CreateManyAsync([
            new CreateIndexModel<Secret>(
                Builders<Secret>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.ResourceGroupId)
                    .Ascending(x => x.IsolationScopeId)
                    .Ascending(x => x.NormalizedName),
                new CreateIndexOptions
                {
                    Unique = true
                }),
            new CreateIndexModel<Secret>(
                Builders<Secret>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.Id)),
            new CreateIndexModel<Secret>(
                Builders<Secret>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.ResourceGroupId)
                    .Ascending(x => x.IsolationScopeId)),
            new CreateIndexModel<Secret>(
                Builders<Secret>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending("Bindings.ConsumerType")
                    .Ascending("Bindings.ConsumerId"))
        ], cancellationToken);

        await context.GetCollection<SecretVersion>("SecretVersions").Indexes.CreateOneAsync(
            new CreateIndexModel<SecretVersion>(
                Builders<SecretVersion>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.SecretId)
                    .Ascending(x => x.Version),
                new CreateIndexOptions
                {
                    Unique = true
                }),
            cancellationToken: cancellationToken);
    }
}
