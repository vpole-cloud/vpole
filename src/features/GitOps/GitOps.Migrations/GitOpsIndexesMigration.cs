using Pole.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.GitOps.Migrations;

public sealed class GitOpsIndexesMigration : IMigration
{
    internal const string ResourceIdentityIndex = "UX_ManagedDeclarations_Tenant_Kind_Resource";

    public ObjectId Id => new("6b700088b213917a38c38005");

    public string Name => "Declarative ownership and operation inventory";

    public string Description => "Tenant-wide ownership plus durable source executions and operation results.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        await context.GetCollection<BsonDocument>("GitRepositorySources").Indexes.CreateManyAsync([
                new(Builders<BsonDocument>.IndexKeys.Ascending("TenantId")
                        .Ascending("CreationKey"),
                    new CreateIndexOptions { Unique = true, Name = "UX_GitRepositorySources_Tenant_Creation" }),
                new(Builders<BsonDocument>.IndexKeys.Ascending("TenantId")
                    .Ascending("Repository.Provider").Ascending("Repository.ConnectionId")
                    .Ascending("Repository.ExternalRepositoryId").Ascending("Repository.Branch"))
            ],
            cancellationToken);
        await context.GetCollection<BsonDocument>("GitOperationPreviews").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys
                .Ascending("Source.Source.TenantId").Ascending("Source.Source.SourceId")
                .Descending("_id")), cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>("GitSynchronizationStates")
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("NextPollAtUtc")),
                cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>("GitReconciliationRuns").Indexes.CreateManyAsync([
                new(Builders<BsonDocument>.IndexKeys.Ascending("Stamp.Source.TenantId")
                    .Ascending("Stamp.Source.SourceId").Descending("Stamp.Generation")),
                new(Builders<BsonDocument>.IndexKeys.Ascending("Stamp.Source.TenantId")
                    .Ascending("Stamp.Source.SourceId").Descending("Stamp.Generation"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = "IX_GitRuns_Tenant_Source_HumanLifecycle_Generation",
                        PartialFilterExpression = new BsonDocument { ["Input._t"] = "GitInputLifecycle",
                            ["Input.Plan.Authorization._t"] = "GitPreviewConfirmed" }
                    }),
                new(Builders<BsonDocument>.IndexKeys.Ascending("State._t")
                    .Ascending("State.Lease.ExpiresAtUtc").Ascending("State.NextAttemptAtUtc"))
            ],
            cancellationToken);
        await context.GetCollection<BsonDocument>("GitAcceptedReferences")
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("Source.TenantId")
                        .Ascending("Target.Kind").Ascending("Target.ResourceId")),
                cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>("ManagedDeclarations").Indexes.CreateManyAsync([
                new(Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Kind").Ascending("ResourceId"),
                    new CreateIndexOptions { Unique = true, Name = ResourceIdentityIndex }),
                new(Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SourceId"))
            ],
            cancellationToken);
        await context.GetCollection<BsonDocument>("GitOperationPlans")
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("Stamp.Run.Source.TenantId")
                        .Ascending("Stamp.Run.RunId")), cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>("GitLifecycleCleanupProofs")
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId")
                    .Ascending("SourceId").Ascending("RunId")),
                cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>("GitOperationReceipts")
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId")
                    .Ascending("SourceId").Ascending("Kind").Ascending("ResourceId")),
                cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>("GitOperationReceipts")
            .Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SourceId")
                        .Ascending("RunId")),
                cancellationToken: cancellationToken);
    }
}
