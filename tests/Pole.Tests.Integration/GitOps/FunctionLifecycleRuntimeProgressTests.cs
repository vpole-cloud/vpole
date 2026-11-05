using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
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
        Assert.True(await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
            item.RunId == run.Id && item.Kind == "FunctionApp" && item.Lifecycle!.Step == GitLifecycleStep.BeginRemoval)
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
        Assert.False(await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
            item.RunId == run.Id && item.Kind == "FunctionApp").AnyAsync());
    }

    private async Task<GitReconciliationRun> RemoveFunctionDuringRuntimeAsync(string progress, string? change)
    {
        await SeedFunctionPlanningCatalog();
        var files = FunctionSnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var function = await CurrentFunction();
        await PrepareLifecycleRuntimePlacementAsync(function.Id);
        var sources = Services.GetRequiredService<GitRepositorySources>();
        var owner = (await sources.LoadAsync(TestData.TenantId, source, default))!;
        repository.Publish(owner.Repository.ExternalRepositoryId,
            [AdoptionManifest("ResourceGroup", files), AdoptionManifest("IsolationScope", files)], "remove-function");
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(new(new(owner.TenantId, owner.Id),
            owner.Revision, new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default)).Value);
        await AssertManagedLifecycleVersionAsync(owner, preview, "FunctionApp", function.NormalizedName, function.Id);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(
            new(owner.TenantId, owner.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        var advanced = false;
        repository.BeforeAccess = async () =>
        {
            if (advanced)
                return;
            advanced = true;
            await AdvanceLifecycleRuntimeAsync(function.Id, progress);
            if (change == "root-configuration")
                await FunctionApps.UpdateOneAsync(item => item.Id == function.Id,
                    Builders<FunctionApp>.Update.Set(item => item.Tags, ImmutableHashSet.Create("external")));
            else if (change is "root-revision" or "planning-fence")
                await FunctionApps.UpdateOneAsync(item => item.Id == function.Id,
                    change == "root-revision" ? Builders<FunctionApp>.Update.Inc(item => item.NextRevisionNumber, 1) :
                    Builders<FunctionApp>.Update.Inc(item => item.PlanningFence, 1));
            else if (change == "route-fence")
                await FunctionRoutes.UpdateOneAsync(item => item.FunctionAppId == function.Id,
                    Builders<FunctionRouteBinding>.Update.Inc(item => item.PlanningFence, 1));
            else if (change is "revision-configuration" or "revision-fence")
                await FunctionRevisions.UpdateOneAsync(item => item.Id == function.PendingRevisionId,
                    change == "revision-configuration" ? Builders<FunctionRevision>.Update.Set(item =>
                        item.Spec.Resources.Limits.CpuMillis, 9999) :
                    Builders<FunctionRevision>.Update.Inc(item => item.PlanningFence, 1));
            else if (change == "reference")
            {
                var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == function.Id)
                    .SingleAsync();
                var binding = SecretBinding.Create(Id(), FunctionResourceDeployment.ResourceType.Value, function.Id,
                    new AllSecretDeployments(), []) with
                { DeclarationName = "manual-function-access" };
                var secret = SecretFactory.CreateMetadata(Id(), function.TenantId, function.ResourceGroupId,
                    placement.IsolationScopeId, "manual-function-secret", "Manual dependency", [], DateTime.UtcNow)
                    .Bind(binding, DateTime.UtcNow);
                await Fixture.Database.GetCollection<Secret>("Secrets").InsertOneAsync(secret);
            }
            else
                await ChangeRuntimeApprovalAsync(change, owner, function.Id);
        };
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(owner.TenantId, owner.Id), default);
        Assert.True(advanced);
        repository.BeforeAccess = null;
        return (await sources.RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
    }
}
