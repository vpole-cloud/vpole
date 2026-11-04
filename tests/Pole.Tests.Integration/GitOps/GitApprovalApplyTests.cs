using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class GitPrunePlanningTests
{
    [Fact]
    public async Task External_mutation_between_approval_and_apply_stops_the_unexecuted_remainder()
    {
        var source = await Create(GitPrunePolicy.Retain);
        await Own(source, gadgets, "kept", "kept-id");
        repository.Files = [Manifest("Gadget", "kept", value: "desired")];
        var scheduled = await ConfirmChanges(source,
            new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove));
        gadgets.BeforeApply = _ => gadgets.Documents.UpdateOneAsync(_ => true,
            Builders<BsonDocument>.Update.Set("Value", "external").Inc("Revision", 1));
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);

        var run = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.Contains(run.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
        Assert.Equal("external", (await gadgets.Documents.Find(_ => true).SingleAsync())["Value"].AsString);
        Assert.Equal(0, await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .CountDocumentsAsync(item => item.RunId == run.Id && item.Blocker == null));
    }

    [Fact]
    public async Task Human_approved_no_op_rechecks_the_approved_version_in_its_transaction()
    {
        var source = await Create(GitPrunePolicy.Retain);
        await Own(source, gadgets, "kept", "kept-id");
        repository.Files = [Manifest("Gadget", "kept")];
        var scheduled = await ConfirmChanges(source,
            new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove));
        await gadgets.Documents.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Inc("Revision", 1));
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);
        var run = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.Contains(run.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
        Assert.Equal(0, gadgets.ApplyCalls);
    }

    [Fact]
    public async Task Receipt_proven_prepare_creation_survives_crash_and_allows_configure_without_false_stale()
    {
        var source = await Create(GitPrunePolicy.Retain);
        await Own(source, widgets, "old", "old-id");
        gadgets.TwoPhase = true;
        repository.Files = [Manifest("Gadget", "new")];
        var scheduled = await ConfirmChanges(source,
            new GitPreviewRemoval([new("Widget", "old", "old-id")]));
        gadgets.AfterApply = operation => operation.Phase == DeclarationPhase.Prepare
            ? throw new InjectedInterruption() : Task.CompletedTask;
        await Assert.ThrowsAsync<InjectedInterruption>(() =>
            Worker.ProcessAsync(new(source.TenantId, source.Id), default));
        var delayed = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.IsType<GitRunDelayed>(delayed.State);
        Assert.False((await gadgets.Documents.Find(_ => true).SingleAsync())["Configured"].AsBoolean);
        gadgets.AfterApply = null;
        await Runs.ReplaceOneAsync(item => item.Id == delayed.Id, delayed with
        {
            State = new GitRunDelayed(1, DateTime.UtcNow.AddMinutes(-1))
        });
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);

        var completed = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(completed.State).Completion);
        Assert.True((await gadgets.Documents.Find(_ => true).SingleAsync())["Configured"].AsBoolean);
        Assert.Equal(2, gadgets.ApplyCalls);
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == completed.Id && item.Lifecycle!.Step == GitLifecycleStep.Apply).ToListAsync();
        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, receipt => Assert.Equal(receipt.ResourceId, receipt.Lifecycle!.Subject.ResourceId));
        Assert.Equal(1, await gadgets.Documents.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Replacement_phase_receipts_name_the_new_identity_without_rebinding_the_old_delete_action()
    {
        var source = await Create(GitPrunePolicy.Retain);
        await Own(source, gadgets, "kept", "old-id");
        gadgets.TwoPhase = true;
        repository.Files = [Manifest("Gadget", "kept", "new-identity")];
        var scheduled = await ConfirmChanges(source,
            new GitPreviewReplacement([new(new("Gadget", "kept", "old-id"), "Gadget", "kept")]));
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);

        var completed = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(completed.State).Completion);
        var current = await gadgets.Documents.Find(_ => true).SingleAsync();
        Assert.NotEqual("old-id", current["_id"].AsString);
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == completed.Id).ToListAsync();
        Assert.Equal("old-id", Assert.Single(receipts, item =>
            item.Lifecycle!.Step == GitLifecycleStep.RemovalCompleted).Lifecycle!.Subject.ResourceId);
        Assert.All(receipts.Where(item => item.Lifecycle!.Step == GitLifecycleStep.Apply), item =>
            Assert.Equal(current["_id"].AsString, item.Lifecycle!.Subject.ResourceId));
    }

    private async Task<GitLifecycleScheduled> ConfirmChanges(RepositorySource source, GitPreviewIntent intent)
    {
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(
            new(new(source.TenantId, source.Id), source.Revision, intent), default)).Value);
        return Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(new(source.TenantId, source.Id),
            preview.Id, preview.Revision, TestData.UserId), default)).Value);
    }

    private sealed class InjectedInterruption : Exception;
}
