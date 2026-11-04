using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.GitOps.Application.Persistence;

public sealed class GitOpsIndexesMigration : IMigration
{
    internal const string ResourceIdentityIndex = "UX_ManagedDeclarations_Tenant_Kind_Resource";

    public ObjectId Id => new("6b700088b213917a38c38005");

    public string Name => "Declarative ownership and operation inventory";

    public string Description => "Tenant-wide ownership plus durable source executions and operation results.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        await context.GetCollection<BsonDocument>(GitOpsCollections.Sources)
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId")
                    .Ascending("CreationKey"), new CreateIndexOptions
                    {
                        Unique = true,
                        Name = ManualSnapshotSourceFilters.CreationKeyIndex
                    }), cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>(GitOpsCollections.Executions)
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Status")
                    .Ascending("NextAttemptAtUtc")), cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>(GitOpsCollections.Inventory).Indexes.CreateManyAsync([
                new(Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Kind").Ascending("ResourceId"),
                    new CreateIndexOptions { Unique = true, Name = ResourceIdentityIndex }),
                new(Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SourceId"))], cancellationToken);
        await context.GetCollection<BsonDocument>(GitOpsCollections.Receipts)
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SourceId")
                    .Ascending("RunId")), cancellationToken: cancellationToken);
    }
}
