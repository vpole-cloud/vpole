using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class FunctionLifecycleRuntimeProgressTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("reference-only")]
    public async Task Function_removal_accepts_active_runtime_progress_after_confirmation(string progress)
    {
        var run = await RemoveFunctionDuringRuntimeAsync(progress, null);
        Assert.True(run.State is GitRunWaitingCleanup, string.Join(",", run.Diagnostics.Select(item => item.Code)));
        Assert.Equal(FunctionAppDesiredState.Deleting, (await CurrentFunction()).DesiredState);
        Assert.True(
            await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
                .Find(item =>
                    item.RunId == run.Id && item.Kind == "FunctionApp" &&
                    item.Lifecycle!.Step == GitLifecycleStep.BeginRemoval)
                .AnyAsync());
    }

    [Theory]
    [InlineData("root-configuration")]
    [InlineData("root-revision")]
    [InlineData("planning-fence")]
    [InlineData("route-fence")]
    [InlineData("revision-configuration")]
    [InlineData("revision-fence")]
    [InlineData("placement-configuration")]
    [InlineData("generation")]
    [InlineData("resource-revision")]
    [InlineData("reference")]
    [InlineData("ownership")]
    [InlineData("source")]
    public async Task Function_runtime_progress_does_not_authorize_configuration_or_dependency_changes(string change)
    {
        var run = await RemoveFunctionDuringRuntimeAsync("complete", change);
        Assert.NotEqual(FunctionAppDesiredState.Deleting, (await CurrentFunction()).DesiredState);
        AssertRuntimeApprovalStopped(run, change);
        Assert.False(
            await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
                .Find(item => item.RunId == run.Id && item.Kind == "FunctionApp")
                .AnyAsync());
    }
}
