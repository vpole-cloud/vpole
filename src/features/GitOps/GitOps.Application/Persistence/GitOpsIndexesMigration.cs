using Pole.Features.GitOps.Domain;
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
        await context.GetCollection<RepositorySource>(RepositoryCollections.Sources).Indexes.CreateManyAsync([
                new(Builders<RepositorySource>.IndexKeys.Ascending(item => item.TenantId)
                        .Ascending(item => item.CreationKey),
                    new CreateIndexOptions { Unique = true, Name = RepositoryCollections.CreationIndex }),
                new(Builders<RepositorySource>.IndexKeys.Ascending(item => item.TenantId)
                    .Ascending(item => item.Repository.Provider).Ascending(item => item.Repository.ConnectionId)
                    .Ascending(item => item.Repository.ExternalRepositoryId).Ascending(item => item.Repository.Branch))
            ],
            cancellationToken);
        await context.GetCollection<GitSynchronizationState>(RepositoryCollections.Synchronization)
            .Indexes.CreateOneAsync(new CreateIndexModel<GitSynchronizationState>(
                    Builders<GitSynchronizationState>.IndexKeys.Ascending(item => item.NextPollAtUtc)),
                cancellationToken: cancellationToken);
        await context.GetCollection<GitReconciliationRun>(RepositoryCollections.Runs).Indexes.CreateManyAsync([
                new(Builders<GitReconciliationRun>.IndexKeys.Ascending(item => item.Stamp.Source.TenantId)
                    .Ascending(item => item.Stamp.Source.SourceId).Descending(item => item.Stamp.Generation)),
                new(Builders<GitReconciliationRun>.IndexKeys.Ascending("State._t")
                    .Ascending("State.Lease.ExpiresAtUtc").Ascending("State.NextAttemptAtUtc"))
            ],
            cancellationToken);
        await context.GetCollection<GitAcceptedReference>(RepositoryCollections.References)
            .Indexes.CreateOneAsync(new CreateIndexModel<GitAcceptedReference>(
                    Builders<GitAcceptedReference>.IndexKeys.Ascending(item => item.Source.TenantId)
                        .Ascending(item => item.Target.Kind).Ascending(item => item.Target.ResourceId)),
                cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>(GitOpsCollections.Inventory).Indexes.CreateManyAsync([
                new(Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Kind").Ascending("ResourceId"),
                    new CreateIndexOptions { Unique = true, Name = ResourceIdentityIndex }),
                new(Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SourceId"))
            ],
            cancellationToken);
        await context.GetCollection<BsonDocument>(GitOpsCollections.Receipts)
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SourceId")
                        .Ascending("RunId")),
                cancellationToken: cancellationToken);
    }
}
