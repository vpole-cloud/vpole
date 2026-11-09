using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class PostgresLifecycleRuntimeProgressTests(MongoReplicaSetFixture fixture) : DeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("reference-only")]
    public async Task Postgres_removal_accepts_active_runtime_progress_after_its_grant_receipt(string progress)
    {
        var run = await RemovePostgresDuringRuntimeAsync(progress, null);
        Assert.True(run.State is GitRunWaitingCleanup, string.Join(",", run.Diagnostics.Select(item => item.Code)));
        Assert.IsType<DeletingPostgresClusterLifecycle>((await PostgresResources.Find(item => item.TenantId == Data.TenantId).SingleAsync())
            .Lifecycle);
        Assert.True(await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
            item.RunId == run.Id && item.Kind == "PostgresCluster" &&
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
    public async Task Postgres_runtime_progress_does_not_authorize_configuration_or_dependency_changes(string change)
    {
        var run = await RemovePostgresDuringRuntimeAsync("complete", change);
        Assert.IsNotType<DeletingPostgresClusterLifecycle>((await PostgresResources.Find(item => item.TenantId == Data.TenantId).SingleAsync())
            .Lifecycle);
        AssertRuntimeApprovalStopped(run, change);
        Assert.False(await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
            item.RunId == run.Id && item.Kind == "PostgresCluster").AnyAsync());
    }
}
