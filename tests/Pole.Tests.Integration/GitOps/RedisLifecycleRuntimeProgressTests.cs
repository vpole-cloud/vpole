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

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class RedisLifecycleRuntimeProgressTests(MongoReplicaSetFixture fixture) : DeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("reference-only")]
    public async Task Redis_removal_accepts_active_runtime_progress_after_confirmation(string progress)
    {
        var run = await RemoveRedisDuringRuntimeAsync(progress, null);
        Assert.True(run.State is GitRunWaitingCleanup, string.Join(",", run.Diagnostics.Select(item => item.Code)));
        Assert.IsType<DeletingRedisLifecycle>((await RedisResources.Find(item => item.TenantId == Data.TenantId).SingleAsync()).Lifecycle);
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
        Assert.IsNotType<DeletingRedisLifecycle>((await RedisResources.Find(item => item.TenantId == Data.TenantId).SingleAsync()).Lifecycle);
        AssertRuntimeApprovalStopped(run, change);
        Assert.False(await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
            item.RunId == run.Id && item.Kind == "RedisService").AnyAsync());
    }
}
