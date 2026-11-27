// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Domain;
using Pole.Features.RedisServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class RedisLifecycleRuntimeProgressTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("reference-only")]
    public async Task Redis_removal_accepts_active_runtime_progress_after_confirmation(string progress)
    {
        var run = await RemoveRedisDuringRuntimeAsync(progress, null);
        Assert.True(run.State is GitRunWaitingCleanup, string.Join(",", run.Diagnostics.Select(item => item.Code)));
        Assert.IsType<DeletingRedisLifecycle>(
            (await RedisResources.Find(item => item.TenantId == Data.TenantId).SingleAsync()).Lifecycle);
        Assert.True(
            await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
                .Find(item =>
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
        Assert.IsNotType<DeletingRedisLifecycle>(
            (await RedisResources.Find(item => item.TenantId == Data.TenantId).SingleAsync()).Lifecycle);
        AssertRuntimeApprovalStopped(run, change);
        Assert.False(
            await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
                .Find(item => item.RunId == run.Id && item.Kind == "RedisService")
                .AnyAsync());
    }
}
