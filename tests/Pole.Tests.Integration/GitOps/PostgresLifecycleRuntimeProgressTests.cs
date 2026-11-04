using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle.Mongo;
using Pole.Features.GitOps.Application.Sources.Mongo;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Contracts;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("reference-only")]
    public async Task Postgres_removal_accepts_active_runtime_progress_after_its_grant_receipt(string progress)
    {
        var run = await RemovePostgresDuringRuntimeAsync(progress, null);
        Assert.True(run.State is GitRunWaitingCleanup, string.Join(",", run.Diagnostics.Select(item => item.Code)));
        Assert.IsType<DeletingPostgresClusterLifecycle>((await PostgresResources.Find(_ => true).SingleAsync())
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
        Assert.IsNotType<DeletingPostgresClusterLifecycle>((await PostgresResources.Find(_ => true).SingleAsync())
            .Lifecycle);
        AssertRuntimeApprovalStopped(run, change);
        Assert.False(await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
            item.RunId == run.Id && item.Kind == "PostgresCluster").AnyAsync());
    }

    private async Task<GitReconciliationRun> RemovePostgresDuringRuntimeAsync(string progress, string? change)
    {
        await SeedPostgresPlanningCatalog();
        var files = PostgresSnapshot();
        var postgres = await InitializePostgres(files);
        await PrepareLifecycleRuntimePlacementAsync(postgres.Id);
        var sources = Services.GetRequiredService<GitRepositorySources>();
        var owner = (await sources.LoadAsync(TestData.TenantId, source, default))!;
        repository.Publish(owner.Repository.ExternalRepositoryId,
            [AdoptionManifest("ResourceGroup", files), AdoptionManifest("IsolationScope", files),
                AdoptionManifest("Secret", files)], "remove-postgres");
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(new(new(owner.TenantId, owner.Id),
            owner.Revision, new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default)).Value);
        await AssertManagedLifecycleVersionAsync(owner, preview, "PostgresCluster", postgres.Name, postgres.Id);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(
            new(owner.TenantId, owner.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        var advanced = false;
        repository.BeforeAccess = async () =>
        {
            if (advanced || !await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
                    item.RunId == scheduled.RunId && item.Kind == "SecretBinding" && item.Blocker == null).AnyAsync())
                return;
            advanced = true;
            await AdvanceLifecycleRuntimeAsync(postgres.Id, progress);
            if (change == "root-configuration")
                await PostgresResources.UpdateOneAsync(item => item.Id == postgres.Id,
                    Builders<PostgresCluster>.Update.Set(item => item.Spec.Instances, postgres.Spec.Instances + 1));
            else if (change is "root-revision" or "planning-fence")
                await PostgresResources.UpdateOneAsync(item => item.Id == postgres.Id,
                    change == "root-revision" ? Builders<PostgresCluster>.Update.Inc(item => item.Revision, 1) :
                    Builders<PostgresCluster>.Update.Inc(item => item.PlanningFence, 1));
            else if (change == "reference")
                Assert.IsType<ResourceConnection>((await Services
                    .GetRequiredService<PersistResourceConnectionCreation>()(
                    new(owner.TenantId, "manual-postgres", PostgresResourceDeployment.ResourceType, postgres.Id,
                        PostgresResourceDeployment.ResourceType, postgres.Id, PostgresConnectionEndpoints.ReadWriteId)))
                    .Value);
            else
                await ChangeRuntimeApprovalAsync(change, owner, postgres.Id);
        };
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(owner.TenantId, owner.Id), default);
        Assert.True(advanced);
        repository.BeforeAccess = null;
        return (await sources.RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
    }

    private async Task PrepareLifecycleRuntimePlacementAsync(string resourceId)
    {
        foreach (var cell in await DbContext.RuntimeCells.Find(_ => true).ToListAsync())
            await DbContext.RuntimeCells.ReplaceOneAsync(item => item.Id == cell.Id,
                cell.MarkReady(cell.DesiredGeneration, DateTime.UtcNow));
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == resourceId).SingleAsync();
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id,
            placement.RequestReconciliation(placement.ResolvedPlacement, true, placement.UpdatedAtUtc));
    }

    private async Task AdvanceLifecycleRuntimeAsync(string resourceId, string progress)
    {
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == resourceId).SingleAsync();
        if (progress == "reference-only")
        {
            await DbContext.ResourceDeployments.UpdateOneAsync(item => item.Id == placement.Id,
                Builders<ResourceDeployment>.Update.Inc(item => item.ReferenceFence, 1));
            return;
        }

        var at = placement.UpdatedAtUtc;
        var claim = Id();
        var started = placement.Start(placement.DesiredGeneration, ResourceDeploymentOperation.Apply,
            claim, at, at.AddMinutes(1));
        var observed = progress == "fail"
            ? started.Fail(started.DesiredGeneration, ResourceDeploymentOperation.Apply, claim,
                "FixtureApplyFailed", "Fixture runtime failure", false, at, at.AddMinutes(2))
            : started.Complete(started.DesiredGeneration, ResourceDeploymentOperation.Apply, claim, [], at);
        Assert.Equal(placement.DesiredGeneration, observed.DesiredGeneration);
        Assert.Equal(placement.ResourceRevision, observed.ResourceRevision);
        Assert.Equal(placement.ReferenceFence, observed.ReferenceFence);
        Assert.Equal(placement.Revision + 2, observed.Revision);
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, observed);
    }

    private async Task AssertManagedLifecycleVersionAsync(RepositorySource owner, GitOperationPreview preview,
        string kind, string name, string resourceId)
    {
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var provider = Services.GetServices<IDeclarativeResourceLifecycleProvider>().Single(item => item.Kind == kind);
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>((await provider.ReadLifecycleAsync(
            new(owner.TenantId, new(new(kind, name), resourceId)), session, default)).Value);
        Assert.Equal(GitLifecycleVersionSemantics.ManagedConfiguration, snapshot.VersionSemantics);
        var action = Assert.Single(preview.Operations, item => item.Resource.ResourceId == resourceId);
        var reader = Services.GetRequiredService<GitLifecyclePreviewReferences>();
        var placement = await DbContext.ResourceDeployments.Find(session, item => item.ResourceId == resourceId)
            .SingleAsync();
        foreach (var effect in new[] { GitLifecycleAction.Remove, GitLifecycleAction.Replace,
                     GitLifecycleAction.Detach, GitLifecycleAction.Retain })
        {
            var observed = (await reader.PreconditionsAsync(new(preview.Source, action with { Action = effect },
                preview.References), session, default))!;
            Assert.DoesNotContain(observed.Versions, item => item.DocumentId == $"ResourceDeployment/{placement.Id}");
            Assert.Contains(observed.Versions, item => item.DocumentId ==
                $"ResourceDeployment/{placement.Id}/DesiredGeneration" && item.Revision == placement.DesiredGeneration);
            Assert.Contains(observed.Versions, item => item.DocumentId ==
                $"ResourceDeployment/{placement.Id}/ResourceRevision" && item.Revision == placement.ResourceRevision);
        }
        var strict = (await reader.PreconditionsAsync(new(preview.Source,
            action with { Action = GitLifecycleAction.Apply }, preview.References), session, default))!;
        Assert.Contains(strict.Versions, item => item.DocumentId == $"ResourceDeployment/{placement.Id}" &&
            item.Revision == placement.Revision);
        await session.CommitTransactionAsync();
    }

    private async Task ChangeRuntimeApprovalAsync(string? change, RepositorySource owner, string resourceId)
    {
        if (change is null)
            return;
        if (change == "ownership")
            await Inventory.UpdateOneAsync(item => item.ResourceId == resourceId,
                Builders<ManagedDeclaration>.Update.Set(item => item.SourceId, Id()));
        else if (change == "source")
            await Services.GetRequiredService<GitRepositorySources>().ChangePolicyAsync(new(owner.TenantId,
                owner.Id, owner.Revision, new(TimeSpan.FromMinutes(6)), owner.Prune), default);
        else
        {
            var update = change switch
            {
                "placement-configuration" => Builders<ResourceDeployment>.Update.Set(item =>
                    item.ResolvedPlacement.Compute.Limits.CpuMillis, 9999),
                "generation" => Builders<ResourceDeployment>.Update.Inc(item => item.DesiredGeneration, 1),
                "resource-revision" => Builders<ResourceDeployment>.Update.Inc(item => item.ResourceRevision, 1),
                _ => throw new ArgumentOutOfRangeException(nameof(change))
            };
            await DbContext.ResourceDeployments.UpdateOneAsync(item => item.ResourceId == resourceId, update);
        }
    }

    private static void AssertRuntimeApprovalStopped(GitReconciliationRun run, string change)
    {
        var finished = Assert.IsType<GitRunFinished>(run.State);
        if (change == "source")
            Assert.Equal(GitRunCompletion.Superseded, finished.Completion);
        else
        {
            Assert.Equal(GitRunCompletion.Blocked, finished.Completion);
            Assert.Contains(run.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
        }
    }
}
