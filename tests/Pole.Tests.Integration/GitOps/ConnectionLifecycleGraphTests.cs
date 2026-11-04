using Pole.Features.ResourceConnections.Contracts;
using Pole.Workloads;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Domain;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.ResourceConnections.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task Connection_environment_removal_has_a_confirmable_real_provider_cleanup_graph()
    {
        var files = WebConnectionSnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var owner = (await Services.GetRequiredService<GitRepositorySources>()
            .LoadAsync(TestData.TenantId, source, default))!;
        repository.Publish(owner.Repository.ExternalRepositoryId, [AdoptionManifest("ResourceGroup", files)], "remove");
        var outcome = await Services.GetRequiredService<GitLifecyclePreviews>().CreateAsync(
            new(new(owner.TenantId, owner.Id), owner.Revision,
                new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default);
        Assert.True(outcome is GitOperationPreview,
            outcome.Value is GitPreviewRejected blocked ? blocked.Failure.GetType().Name : outcome.Value.ToString());
        var preview = Assert.IsType<GitOperationPreview>(outcome.Value);
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(
            new(new(owner.TenantId, owner.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(owner.TenantId, owner.Id), default);
        var pending = (await Services.GetRequiredService<GitRepositorySources>()
            .RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
        Assert.True(pending.State is GitRunWaitingCleanup,
            string.Join(",", pending.Diagnostics.Select(item => item.Code)));
        Assert.All(await WebServices.Find(_ => true).ToListAsync(), item =>
            Assert.IsType<DeletingWebServiceLifecycle>(item.Lifecycle));
        var connection = await Connections.Find(_ => true).SingleAsync();
        foreach (var target in Assert.IsType<DeletingResourceConnection>(connection.Lifecycle).Targets)
            await Services.GetRequiredService<RecordResourceConnectionCleanup>()(connection.TenantId, connection.Id,
                target.Snapshot.PolicyLocation, connection.Generation, null);
        Assert.Equal(0, await Connections.CountDocumentsAsync(_ => true));
        foreach (var web in await WebServices.Find(_ => true).ToListAsync())
        {
            var deployments = await DbContext.ResourceDeployments.Find(item => item.ResourceId == web.Id).ToListAsync();
            foreach (var deployment in deployments)
            {
                var now = deployment.UpdatedAtUtc.AddSeconds(1);
                var claim = Id();
                var active = deployment.Start(deployment.DesiredGeneration, ResourceDeploymentOperation.Delete,
                    claim, now, now.AddMinutes(2));
                await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id,
                    active.Complete(deployment.DesiredGeneration, ResourceDeploymentOperation.Delete,
                        claim, [], now.AddSeconds(1)));
            }
            Assert.IsType<WebServiceDeletionFinalized>((await Services.GetRequiredService<FinalizeWebServiceDeletion>()(
                web.TenantId, web.Id)).Value);
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var run = (await Services.GetRequiredService<GitRepositorySources>()
                .RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
            if (run.State is GitRunFinished)
                break;
            await ResumeConnectionLifecycleAsync(run);
        }
        var complete = (await Services.GetRequiredService<GitRepositorySources>()
            .RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(complete.State).Completion);
        Assert.Equal(0, complete.ConsecutiveFailures);
        Assert.Equal("ResourceGroup", (await Inventory.Find(_ => true).SingleAsync()).Kind);
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == complete.Id).ToListAsync();
        Assert.Equal(3, receipts.Count(item => item.Lifecycle?.Step == GitLifecycleStep.BeginRemoval));
        Assert.Equal(3, receipts.Count(item => item.Lifecycle?.Step == GitLifecycleStep.RemovalCompleted));
        Assert.Equal(RepositoryExecutionDisposition.NoWork, (await Services.GetRequiredService<RepositorySourceJob>()
            .ProcessAsync(new(owner.TenantId, owner.Id), default)).Disposition);

    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Manual_or_foreign_connection_blocks_parent_preview_even_when_already_deleting(
        bool foreign, bool deleting)
    {
        var files = WebConnectionSnapshot();
        await Apply(files);
        var connection = await Connections.Find(_ => true).SingleAsync();
        if (foreign)
            await Inventory.UpdateOneAsync(item => item.ResourceId == connection.Id,
                Builders<ManagedDeclaration>.Update.Set(item => item.SourceId, Id()));
        else
            await Inventory.DeleteOneAsync(item => item.ResourceId == connection.Id);
        if (deleting)
            await Connections.ReplaceOneAsync(item => item.Id == connection.Id,
                connection.BeginDeletion(DateTime.UtcNow));
        var owner = (await Services.GetRequiredService<GitRepositorySources>()
            .LoadAsync(TestData.TenantId, source, default))!;
        repository.Publish(owner.Repository.ExternalRepositoryId, [AdoptionManifest("ResourceGroup", files)], "remove");
        var result = await Services.GetRequiredService<GitLifecyclePreviews>().CreateAsync(
            new(new(owner.TenantId, owner.Id), owner.Revision,
                new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default);
        Assert.IsType<GitLifecycleDependencyRetained>(Assert.IsType<GitPreviewRejected>(result.Value).Failure);
        Assert.Equal(connection.Revision + (deleting ? 1 : 0),
            (await Connections.Find(item => item.Id == connection.Id).SingleAsync()).Revision);
    }
    [Fact]
    public async Task A_connection_added_after_confirmation_is_never_cascaded_by_the_reviewed_parent()
    {
        var files = WebConnectionSnapshot();
        await Apply(files);
        var sources = Services.GetRequiredService<GitRepositorySources>();
        var owner = (await sources.LoadAsync(TestData.TenantId, source, default))!;
        repository.Publish(owner.Repository.ExternalRepositoryId, [AdoptionManifest("ResourceGroup", files)], "remove");
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(
            new(new(owner.TenantId, owner.Id), owner.Revision,
                new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default)).Value);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(
            new(new(owner.TenantId, owner.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        var web = await WebServices.Find(item => item.Name == "demo-api").SingleAsync();
        var backend = await WebServices.Find(item => item.Name == "backend").SingleAsync();
        var added = Assert.IsType<ResourceConnection>((await
            Services.GetRequiredService<PersistResourceConnectionCreation>()(
            new(owner.TenantId, "unreviewed", ResourceTypeId.Create("pole/web-service"), web.Id,
                ResourceTypeId.Create("pole/web-service"), backend.Id, "http"))).Value);
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(owner.TenantId, owner.Id), default);
        var run = (await sources.RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.Contains(run.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
        Assert.IsType<ActiveResourceConnection>((await Connections.Find(item => item.Id == added.Id).SingleAsync())
            .Lifecycle);
        Assert.All(await WebServices.Find(_ => true).ToListAsync(), item =>
            Assert.IsNotType<DeletingWebServiceLifecycle>(item.Lifecycle));
    }

    private async Task ResumeConnectionLifecycleAsync(GitReconciliationRun run)
    {
        var waiting = Assert.IsType<GitRunWaitingCleanup>(run.State);
        await Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>().ReplaceOneAsync(
            item => item.Id == run.Id, run with
            { State = new GitRunWaitingCleanup(waiting.LastAttempt, DateTime.UtcNow.AddMinutes(-1)) });
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(run.Stamp.Source, default);
    }

}
