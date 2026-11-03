using Pole.Features.GitOps.Application;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Quotas.Domain;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.RedisServices.Migrations;
using Pole.Features.RedisServices.Application.Persistence.Mutations;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.StorageProfiles.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    private IMongoCollection<Redis> RedisResources => Fixture.Database.GetCollection<Redis>("RedisServices");

    private async Task SeedRedisPlanningCatalog()
    {
        await new RedisSchemaValidationMigration().Up(Fixture.Database);
        await new RedisNameIndexesMigration().Up(Fixture.Database);
        var computes = Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
        var compute = await computes.Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        compute = compute with { AllowedResourceTypes = compute.AllowedResourceTypes.Add("pole/redis") };
        await computes.ReplaceOneAsync(item => item.Id == compute.Id, compute);
        await Fixture.Database.GetCollection<ComputeProfileRevision>("ComputeProfileRevisions").ReplaceOneAsync(
            item => item.Id == $"{compute.Id}/1", ComputeProfileRevisionFactory.Create(compute),
            new ReplaceOptions { IsUpsert = true });
        var storage = (await DbContext.StorageProfiles.Find(item => item.Id == TestData.DefaultStorageProfileId)
            .SingleAsync()) with
        { TechnicalName = "redis-storage", AllowedResourceTypes = ["pole/redis"] };
        await DbContext.StorageProfiles.ReplaceOneAsync(item => item.Id == storage.Id, storage);
        await Fixture.Database.GetCollection<StorageProfileRevision>("StorageProfileRevisions")
            .InsertOneAsync(StorageProfileRevisionFactory.Create(storage));
    }

    private static ManifestFile[] RedisSnapshot(
        int size = 4,
        string? scope = "private-cache",
        long storageRevision = 1) =>
    [
        new("redis.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: RedisService
            metadata:
              name: cache
            spec:
              resourceGroup: redis-group
              placement:
                deploymentTarget: lab
                computeProfile: { name: small, revision: 1 }
                {{(scope is null ? "" : $"isolationScope: {scope}")}}
              storage:
                sizeGb: {{size}}
                profile: { name: redis-storage, revision: {{storageRevision}} }
              tags: [production, cache]
            ---
            apiVersion: pole.io/v1alpha1
            kind: IsolationScope
            metadata:
              name: private-cache
            spec:
              resourceGroup: redis-group
            ---
            apiVersion: pole.io/v1alpha1
            kind: ResourceGroup
            metadata:
              name: redis-group
            spec:
              description: Redis group
              tags: []
            """)
    ];

    [Fact]
    public async Task RedisSameCommitRepairsTagsAndComputeThenNoOpPreservesBusinessState()
    {
        await SeedRedisPlanningCatalog();
        var files = RedisSnapshot();
        var created = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, created.Status);
        var resource = await RedisResources.Find(Builders<Redis>.Filter.Empty).SingleAsync();
        Assert.IsType<ActiveRedisLifecycle>(resource.Lifecycle);
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == resource.Id).SingleAsync();
        var scope = await DbContext.IsolationScopes.Find(item => item.Name == "private-cache").SingleAsync();
        Assert.Equal(scope.Id, placement.IsolationScopeId);
        Assert.Equal(1, placement.ResolvedPlacement.Storage!.StorageProfileRevision);
        Assert.Equal("standard", placement.ResolvedPlacement.Storage.StorageClassName);
        await RedisResources.ReplaceOneAsync(item => item.Id == resource.Id, resource with
        {
            Revision = resource.Revision + 1,
            Tags = ImmutableHashSet.Create("drift-private-value")
        });
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, placement with
        {
            Revision = placement.Revision + 1,
            ResolvedPlacement = placement.ResolvedPlacement with
            {
                Compute = placement.ResolvedPlacement.Compute with
                { Limits = placement.ResolvedPlacement.Compute.Limits with { CpuMillis = 9999 } }
            }
        });
        var downloads = repository.ReadCommits.Count;
        var repairedRun = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, repairedRun.Status);
        Assert.Equal(downloads, repository.ReadCommits.Count);
        var repaired = await RedisResources.Find(item => item.Id == resource.Id).SingleAsync();
        Assert.True(repaired.Tags.SetEquals(resource.Tags));
        Assert.Equal(placement.ResolvedPlacement.Compute.Limits,
            (await DbContext.ResourceDeployments.Find(item => item.Id == placement.Id).SingleAsync())
            .ResolvedPlacement.Compute.Limits);
        var operations = (await Services.GetRequiredService<GitRepositoryQueries>().OperationsAsync(
            TestData.TenantId, source, repairedRun.Context!.RunId, 1, 20, default))!.Items;
        var configure = operations.Single(item => item.Kind == "RedisService" &&
            item.Phase == DeclarationPhase.Configure).Plan!;
        Assert.Equal(GitPlannedAction.Update, configure.Action);
        Assert.Contains("configuration.tags", configure.ChangedFields);
        Assert.Contains("configuration.compute.limits.cpuMillis", configure.ChangedFields);
        Assert.DoesNotContain("drift-private-value", System.Text.Json.JsonSerializer.Serialize(operations));

        var events = await BusinessEvents();
        var stableRun = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, stableRun.Status);
        var stable = await RedisResources.Find(item => item.Id == resource.Id).SingleAsync();
        Assert.Equal(repaired.Revision, stable.Revision);
        Assert.Equal(events, await BusinessEvents());
        var view = await Services.GetRequiredService<GitRepositoryQueries>().RunAsync(TestData.TenantId, source,
            stableRun.Context!.RunId, default);
        Assert.Equal(RepositoryRunStatus.AlreadyCurrent, view!.Status);
        Assert.Equal(new RepositoryOperationSummary(0, 0, 4, 0, 0, null), view.Operations);
        var detail = await ActivatorUtilities.CreateInstance<GetRedisServiceByIdHandler>(Services)
            .HandleAsync(new(resource.Id, resource.TenantId));
        Assert.NotNull(Assert.IsType<RedisDto>(detail.Value).GitOps);
        var update = await ActivatorUtilities.CreateInstance<UpdateRedisServiceHandler>(Services)
                        .HandleAsync(new(resource.Id,
                new(resource.ToDto().Spec, ImmutableHashSet.Create("manual")), resource.TenantId));
        Assert.IsType<RedisGitOwnedConfiguration>(update.Value);
        var delete = await ActivatorUtilities.CreateInstance<DeleteRedisServiceHandler>(Services)
            .HandleAsync(new(resource.Id, resource.TenantId));
        Assert.IsType<RedisGitOwnedConfiguration>(delete.Value);
        Assert.Equal(events, await BusinessEvents());
    }

    [Theory]
    [InlineData("size")]
    [InlineData("scope")]
    [InlineData("storageRevision")]
    public async Task RedisImmutablePlacementChangesAreBlocked(string field)
    {
        await SeedRedisPlanningCatalog();
        await Apply(RedisSnapshot());
        var original = await RedisResources.Find(Builders<Redis>.Filter.Empty).SingleAsync();
        var storage = await DbContext.StorageProfiles.Find(item => item.Id == TestData.DefaultStorageProfileId)
            .SingleAsync();
        var updated = storage with { Revision = 2 };
        await DbContext.StorageProfiles.ReplaceOneAsync(item => item.Id == storage.Id, updated);
        await Fixture.Database.GetCollection<StorageProfileRevision>("StorageProfileRevisions")
            .InsertOneAsync(StorageProfileRevisionFactory.Create(updated));
        var result = await Apply(RedisSnapshot(size: field == "size" ? 5 : 4,
            scope: field == "scope" ? null : "private-cache", storageRevision: field == "storageRevision" ? 2 : 1));
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        var unchanged = await RedisResources.Find(item => item.Id == original.Id).SingleAsync();
        Assert.Equal(original.Revision, unchanged.Revision);
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == result.Context!.RunId && item.Kind == "RedisService").ToListAsync();
        Assert.Contains(receipts, item => item.Blocker == nameof(DeclarationReplacementRequired));
    }

    [Fact]
    public async Task RedisMissingCreatedIdentityIsNotRecreatedOrRebound()
    {
        await SeedRedisPlanningCatalog();
        await Apply(RedisSnapshot());
        var original = await RedisResources.Find(Builders<Redis>.Filter.Empty).SingleAsync();
        await RedisResources.DeleteOneAsync(item => item.Id == original.Id);
        var result = await Apply(RedisSnapshot());
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(0, await RedisResources.CountDocumentsAsync(Builders<Redis>.Filter.Empty));
        await RedisResources.InsertOneAsync(original with { Id = ObjectId.GenerateNewId().ToString() });
        result = await Apply(RedisSnapshot());
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(0, await RedisResources.CountDocumentsAsync(item => item.Id == original.Id));
    }

    [Fact]
    public async Task RedisPinnedHistoricalStorageUsesCurrentPolicyAndDoesNotMigrateClass()
    {
        await SeedRedisPlanningCatalog();
        var storage = await DbContext.StorageProfiles.Find(item => item.Id == TestData.DefaultStorageProfileId)
            .SingleAsync();
        await DbContext.StorageProfiles.ReplaceOneAsync(item => item.Id == storage.Id, storage with
        {
            Revision = 2,
            TargetMappings = [StorageProfileTargetMapping.Create(TestData.DefaultDeploymentTargetId, "fast")]
        });
        var result = await Apply(RedisSnapshot());
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, result.Status);
        var resource = await RedisResources.Find(Builders<Redis>.Filter.Empty).SingleAsync();
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == resource.Id).SingleAsync();
        Assert.Equal("standard", placement.ResolvedPlacement.Storage!.StorageClassName);
        await DbContext.StorageProfiles.UpdateOneAsync(item => item.Id == storage.Id,
            Builders<StorageProfile>.Update.Set(item => item.IsEnabled, false).Inc(item => item.Revision, 1));
        var events = await BusinessEvents();
        result = await Apply(RedisSnapshot());
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(events, await BusinessEvents());
    }
    [Fact]
    public async Task RedisNoOpReplansWhenResourceChangesAfterPlanning()
    {
        await SeedRedisPlanningCatalog();
        var files = RedisSnapshot();
        await Apply(files);
        var original = await RedisResources.Find(Builders<Redis>.Filter.Empty).SingleAsync();
        var intercepted = false;
        var mutations = new BeforeNoOp(Services.GetRequiredService<IGitOpsMutations>(), async (operation, ct) =>
        {
            if (intercepted || operation.Declaration.Address.Kind != "RedisService" ||
                operation.Phase != DeclarationPhase.Configure)
                return;

            intercepted = true;
            await RedisResources.UpdateOneAsync(item => item.Id == original.Id,
                Builders<Redis>.Update.Set(item => item.Tags, ImmutableHashSet.Create("racing-change"))
                    .Inc(item => item.Revision, 1), cancellationToken: ct);
        });
        var result = await new RepositoryPipelineDriver(Services, Fixture, repository, customMutations: mutations)
            .SubmitAsync(new(TestData.TenantId, source), files);
        Assert.True(intercepted);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, result.Status);
        Assert.True(original.Tags.SetEquals((await RedisResources.Find(item => item.Id == original.Id)
            .SingleAsync()).Tags));
        var operations = (await Services.GetRequiredService<GitRepositoryQueries>().OperationsAsync(TestData.TenantId,
            source, result.Context!.RunId, 1, 20, default))!.Items;
        var plan = operations.Single(item => item.Kind == "RedisService" &&
            item.Phase == DeclarationPhase.Configure).Plan!;
        Assert.Equal(2, plan.Revision);
        Assert.Equal(GitPlannedAction.Update, plan.Action);
    }

    [Fact]
    public async Task RedisPrepareRollsBackRootPlacementRegistrationQuotaAndEventsOnPlacementFailure()
    {
        await SeedRedisPlanningCatalog();
        var original = Services.GetRequiredService<CreateResourceDeployment>();
        var called = false;
        CreateResourceDeployment rejectAfterCreate = async (request, session, ct) =>
        {
            var result = await original(request, session, ct);
            Assert.IsType<PlacementSummaryDto>(result.Value);
            var prepared = await RedisResources.Find(session, item => item.Id == request.ResourceId).SingleAsync(ct);
            Assert.IsType<PreparedRedisLifecycle>(prepared.Lifecycle);
            var placement = await DbContext.ResourceDeployments.Find(session, item => item.Id == request.DeploymentId)
                .SingleAsync(ct);
            Assert.IsType<PreparedResourceDeploymentState>(placement.State);
            called = true;
            return new CreateResourceDeploymentError(new ResourcePlacementUnavailable(request.ResourceGroupId));
        };
        var providers = Services.GetServices<IDeclarativeResourceProvider>().ToArray();
        var redisProvider = providers.Single(item => item.Kind == "RedisService");
        var executionType = redisProvider.GetType().Assembly.GetType(
            "Pole.Features.RedisServices.Application.Persistence.RedisDeclarationExecution", throwOnError: true)!;
        var execution = ActivatorUtilities.CreateInstance(Services, executionType, rejectAfterCreate);
        var failing = (IDeclarativeResourceProvider)ActivatorUtilities.CreateInstance(Services,
            redisProvider.GetType(), execution);
        var registry = new DeclarativeProviderRegistry(providers.Select(item =>
            item.Kind == "RedisService" ? failing : item));
        var result = await new RepositoryPipelineDriver(Services, Fixture, repository, customProviders: registry)
            .SubmitAsync(new(TestData.TenantId, source), RedisSnapshot());
        Assert.True(called);
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(0, await RedisResources.CountDocumentsAsync(Builders<Redis>.Filter.Empty));
        Assert.Equal(0, await DbContext.ResourceDeployments.CountDocumentsAsync(item =>
            item.ResourceType == "pole/redis"));
        Assert.Equal(0, await DbContext.ResourceRegistrations.CountDocumentsAsync(item => item.DisplayName == "cache"));
        Assert.Equal(0, await DbContext.OutboxMessages.CountDocumentsAsync(item =>
            item.EventType == "pole.redis/v1/redis-service-created"));
        var owned = await Inventory.Find(item => item.Kind == "RedisService").SingleAsync();
        Assert.Equal(GitResourceMaterialization.Allocated, owned.Materialization);
        Assert.Null(owned.Projections.Applied);
        var receipt = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == result.Context!.RunId && item.Kind == "RedisService" &&
                item.Phase == nameof(DeclarationPhase.Prepare)).SingleAsync();
        var usages = await Fixture.Database.GetCollection<TenantQuotaUsage>("TenantQuotaUsages")
            .Find(item => item.TenantId == TestData.TenantId).ToListAsync();
        Assert.DoesNotContain(usages.SelectMany(item => item.Values), item =>
            item.Key.Value == "RedisServiceCount" && item.Value != 0);
        Assert.False(receipt.Changed);
        Assert.Equal("PlacementUnavailable", receipt.Blocker);
    }

}
