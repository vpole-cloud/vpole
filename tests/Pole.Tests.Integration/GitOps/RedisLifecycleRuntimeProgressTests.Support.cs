using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected async Task<GitReconciliationRun> RemoveRedisDuringRuntimeAsync(string progress, string? change)
    {
        await SeedRedisPlanningCatalog();
        var files = RedisSnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var redis = await RedisResources.Find(item => item.TenantId == Data.TenantId).SingleAsync();
        await PrepareLifecycleRuntimePlacementAsync(redis.Id);
        var sources = Services.GetRequiredService<GitRepositorySources>();
        var owner = (await sources.LoadAsync(Data.TenantId, source, default))!;
        repository.Publish(owner.Repository.ExternalRepositoryId,
            [AdoptionManifest("ResourceGroup", files), AdoptionManifest("IsolationScope", files)], "remove-redis");
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(new(new(owner.TenantId, owner.Id),
            owner.Revision, new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default)).Value);
        await AssertManagedLifecycleVersionAsync(owner, preview, "RedisService", redis.Name, redis.Id);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(
            new(owner.TenantId, owner.Id), preview.Id, preview.Revision, Data.UserId), default)).Value);
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
