using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Migrations;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class GitPrunePlanningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_requires_fresh_approval_for_remaining_human_effects_but_not_a_completed_plan(
        bool allEffectsComplete)
    {
        await new GitOpsIndexesMigration().Up(Fixture.Database);
        var source = await Create(GitPrunePolicy.Retain);
        await Own(source, gadgets, "kept", "old-id");
        gadgets.TwoPhase = true;
        repository.Files = [Manifest("Gadget", "kept", "replacement")];
        var scheduled = await ConfirmChanges(source,
            new GitPreviewReplacement([new(new("Gadget", "kept", "old-id"), "Gadget", "kept")]));
        var interruptedPhase = allEffectsComplete ? DeclarationPhase.Configure : DeclarationPhase.Prepare;
        gadgets.AfterApply = operation => operation.Phase == interruptedPhase
            ? throw new InjectedInterruption() : Task.CompletedTask;
        await Assert.ThrowsAsync<InjectedInterruption>(() => Worker.ProcessAsync(new(source.TenantId, source.Id),
            default));
        var interrupted = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.IsType<GitRunDelayed>(interrupted.State);
        var prepared = await gadgets.Documents.Find(_ => true).SingleAsync();
        Assert.NotEqual("old-id", prepared["_id"].AsString);
        Assert.Equal(allEffectsComplete, prepared["Configured"].AsBoolean);
        var calls = gadgets.ApplyCalls;
        var resumed = await PauseThenResumeAsync(source);
        repository.Commit = new string('b', 40);
        repository.Files = [Manifest("Gadget", "kept", "replacement", "current-branch")];
        gadgets.AfterApply = null;
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);

        var latest = await Latest(resumed);
        Assert.Equal(repository.Commit, latest.Commit);
        if (!allEffectsComplete)
        {
            Assert.Equal(calls, gadgets.ApplyCalls);
            Assert.False((await gadgets.Documents.Find(_ => true).SingleAsync())["Configured"].AsBoolean);
            var awaiting = Assert.IsType<GitInputAwaitingApproval>(latest.Input);
            var preview = await Services.GetRequiredService<IMongoCollection<GitOperationPreview>>()
                .Find(item => item.Id == awaiting.PreviewId).SingleAsync();
            Assert.Equal(resumed.Revision, preview.Source.ConfigurationRevision);
            Assert.Equal(repository.Commit, Assert.IsType<GitPreviewRepositoryInput>(preview.Source.Input)
                .Snapshot.Commit);
            var next = Assert.IsType<GitLifecycleScheduled>((await Services.GetRequiredService<GitLifecyclePreviews>()
                .ConfirmAsync(new(new(source.TenantId, source.Id), preview.Id, preview.Revision, TestData.UserId),
                    default)).Value);
            Assert.NotEqual(scheduled.RunId, next.RunId);
            await Worker.ProcessAsync(new(source.TenantId, source.Id), default);
            latest = await Latest(resumed);
        }
        else
            Assert.IsType<GitInputAccepted>(latest.Input);

        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(latest.State).Completion);
        Assert.Equal("current-branch", (await gadgets.Documents.Find(_ => true).SingleAsync())["Value"].AsString);
        Assert.True((await gadgets.Documents.Find(_ => true).SingleAsync())["Configured"].AsBoolean);
        repository.Commit = new string('c', 40);
        repository.Files = [Manifest("Gadget", "kept", "replacement", "later-sync")];
        await Sources.RequestSyncAsync(new(source.TenantId, source.Id, resumed.Revision, "later-sync",
            new(GitRunTrigger.Manual, TestData.UserId)), default);
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);
        var later = await Latest(resumed);
        Assert.IsType<GitInputAccepted>(later.Input);
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(later.State).Completion);
        Assert.Equal("later-sync", (await gadgets.Documents.Find(_ => true).SingleAsync())["Value"].AsString);
    }

    [Fact]
    public async Task An_interrupted_policy_authorized_plan_does_not_require_human_approval_after_resume()
    {
        var source = await Create();
        await Own(source, widgets, "old", "old-id");
        gadgets.TwoPhase = true;
        repository.Files = [Manifest("Gadget", "new")];
        gadgets.AfterApply = operation => operation.Phase == DeclarationPhase.Prepare
            ? throw new InjectedInterruption() : Task.CompletedTask;
        await Assert.ThrowsAsync<InjectedInterruption>(() => Worker.ProcessAsync(new(source.TenantId, source.Id),
            default));
        Assert.IsType<GitPreviewPolicyAuthorized>(Assert.IsType<GitInputLifecycle>((await Latest(source)).Input)
            .Plan.Authorization);
        var resumed = await PauseThenResumeAsync(source);
        repository.Commit = new string('b', 40);
        gadgets.AfterApply = null;
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);

        var run = await Latest(resumed);
        Assert.Equal(repository.Commit, run.Commit);
        Assert.IsType<GitInputAccepted>(run.Input);
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.True((await gadgets.Documents.Find(_ => true).SingleAsync())["Configured"].AsBoolean);
    }

    private async Task<RepositorySource> PauseThenResumeAsync(RepositorySource source)
    {
        var lifecycle = Services.GetRequiredService<GitSourceLifecycleStore>();
        var paused = Assert.IsType<RepositorySource>((await lifecycle.PauseAsync(
            new(source.TenantId, source.Id, source.Revision, TestData.UserId), default)).Value);
        return Assert.IsType<RepositorySource>((await lifecycle.ResumeAsync(
            new(source.TenantId, source.Id, paused.Revision, TestData.UserId), default)).Value);
    }
}
