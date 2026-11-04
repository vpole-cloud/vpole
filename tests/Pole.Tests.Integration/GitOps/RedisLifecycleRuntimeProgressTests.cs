using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Lifecycle.Mongo;
using Pole.Features.GitOps.Application.Sources.Mongo;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("reference-only")]
    public async Task Redis_removal_accepts_active_runtime_progress_after_confirmation(string progress)
    {
        var run = await RemoveRedisDuringRuntimeAsync(progress, null);
        Assert.True(run.State is GitRunWaitingCleanup, string.Join(",", run.Diagnostics.Select(item => item.Code)));
        Assert.IsType<DeletingRedisLifecycle>((await RedisResources.Find(_ => true).SingleAsync()).Lifecycle);
        Assert.True(await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
            item.RunId == run.Id && item.Kind == "RedisService" &&
            item.Lifecycle!.Step == GitLifecycleStep.BeginRemoval)
            .AnyAsync());
    }

    [Theory]
    [InlineData("root-configuration")]
    [InlineData("root-revision")]
    [InlineData("planning-fence")]
    [InlineData("placement-configuration")]
    [InlineData("generation")]
    [InlineData("resource-revision")]
    [InlineData("reference")]
    [InlineData("ownership")]
    [InlineData("source")]
    public async Task Redis_runtime_progress_does_not_authorize_configuration_or_dependency_changes(string change)
    {
        var run = await RemoveRedisDuringRuntimeAsync("complete", change);
        Assert.IsNotType<DeletingRedisLifecycle>((await RedisResources.Find(_ => true).SingleAsync()).Lifecycle);
        AssertRuntimeApprovalStopped(run, change);
        Assert.False(await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
            item.RunId == run.Id && item.Kind == "RedisService").AnyAsync());
    }

    private async Task<GitReconciliationRun> RemoveRedisDuringRuntimeAsync(string progress, string? change)
    {
        await SeedRedisPlanningCatalog();
        var files = RedisSnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var redis = await RedisResources.Find(_ => true).SingleAsync();
        await PrepareLifecycleRuntimePlacementAsync(redis.Id);
        var sources = Services.GetRequiredService<GitRepositorySources>();
        var owner = (await sources.LoadAsync(TestData.TenantId, source, default))!;
        repository.Publish(owner.Repository.ExternalRepositoryId,
            [AdoptionManifest("ResourceGroup", files), AdoptionManifest("IsolationScope", files)], "remove-redis");
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(new(new(owner.TenantId, owner.Id),
            owner.Revision, new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default)).Value);
        await AssertManagedLifecycleVersionAsync(owner, preview, "RedisService", redis.Name, redis.Id);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(
            new(owner.TenantId, owner.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        var advanced = false;
        repository.BeforeAccess = async () =>
        {
            if (advanced)
                return;
            advanced = true;
            await AdvanceLifecycleRuntimeAsync(redis.Id, progress);
            if (change == "root-configuration")
                await RedisResources.UpdateOneAsync(item => item.Id == redis.Id,
                    Builders<Redis>.Update.Set(item => item.Tags, ImmutableHashSet.Create("external")));
            else if (change is "root-revision" or "planning-fence")
                await RedisResources.UpdateOneAsync(item => item.Id == redis.Id,
                    change == "root-revision" ? Builders<Redis>.Update.Inc(item => item.Revision, 1) :
                    Builders<Redis>.Update.Inc(item => item.PlanningFence, 1));
            else if (change == "reference")
                Assert.IsType<ResourceConnection>((await Services
                    .GetRequiredService<PersistResourceConnectionCreation>()(
                    new(owner.TenantId, "manual-redis", RedisResourceDeployment.ResourceType, redis.Id,
                        RedisResourceDeployment.ResourceType, redis.Id, RedisConnectionEndpoints.Redis.Id))).Value);
            else
                await ChangeRuntimeApprovalAsync(change, owner, redis.Id);
        };
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(owner.TenantId, owner.Id), default);
        Assert.True(advanced);
        repository.BeforeAccess = null;
        return (await sources.RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
    }
}
