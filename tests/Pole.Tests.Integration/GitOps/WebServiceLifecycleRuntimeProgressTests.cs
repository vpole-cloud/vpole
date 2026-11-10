using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Global4)]
public sealed class WebServiceLifecycleRuntimeProgressTests(
    MongoReplicaSetFixture fixture
) : IsolatedDeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("observation-fence")]
    public async Task Hostname_removal_receipt_survives_its_active_ingress_runtime_progress(string progress)
    {
        var run = await RemoveHostnameThenAdvanceRuntime(progress, null);
        Assert.True(run.State is GitRunWaitingCleanup, string.Join(",", run.Diagnostics.Select(item => item.Code)));
        Assert.IsType<DeletingWebServiceLifecycle>((await Service()).Lifecycle);
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == run.Id)
            .ToListAsync();
        Assert.Single(
            receipts,
            item => item.Kind == "HostnameBinding" && item.Lifecycle?.Step == GitLifecycleStep.RemovalCompleted);
        Assert.Single(
            receipts,
            item => item.Kind == "WebService" && item.Lifecycle?.Step == GitLifecycleStep.BeginRemoval);
        var placement = await DbContext.ResourceDeployments.Find(_ => true).SingleAsync();
        Assert.Equal(ResourceDeploymentOperation.Delete, placement.DesiredOperation);
    }

    [Theory]
    [InlineData("root-configuration")]
    [InlineData("root-revision")]
    [InlineData("placement-configuration")]
    [InlineData("generation")]
    [InlineData("resource-revision")]
    [InlineData("reference")]
    [InlineData("ownership")]
    [InlineData("source")]
    public async Task Runtime_progress_does_not_authorize_new_configuration_references_or_ownership(string change)
    {
        var run = await RemoveHostnameThenAdvanceRuntime("complete", change);
        Assert.IsType<ActiveWebServiceLifecycle>((await Service()).Lifecycle);
        var finished = Assert.IsType<GitRunFinished>(run.State);
        if (change == "source")
        {
            Assert.Equal(GitRunCompletion.Superseded, finished.Completion);
        }
        else
        {
            Assert.Equal(GitRunCompletion.Blocked, finished.Completion);
            Assert.Contains(run.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
        }

        Assert.False(
            await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
                .Find(item => item.RunId == run.Id && item.Kind == "WebService")
                .AnyAsync());
    }
}
