using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class GitPrunePlanningTests
{
    [Fact]
    public async Task Retain_confirmation_allows_independent_create_without_deleting_or_releasing_old_identity()
    {
        var source = await Create(GitPrunePolicy.Retain);
        await Own(source, widgets, "old", "old-id");
        repository.Files = [Manifest("Widget", "next")];
        await Worker.ProcessAsync(new GitSourceIdentity(source.TenantId, source.Id), default);
        var waiting = await Latest(source);
        var awaiting = Assert.IsType<GitInputAwaitingApproval>(waiting.Input);
        Assert.Equal(0, widgets.ApplyCalls);
        Assert.Equal(1, await widgets.Documents.CountDocumentsAsync(_ => true));
        var previews = Services.GetRequiredService<IMongoCollection<GitOperationPreview>>();
        var preview = await previews.Find(item => item.Id == awaiting.PreviewId).SingleAsync();
        Assert.Equal(
            GitPreviewRemovalDisposition.Retain,
            Assert.IsType<GitPreviewResourceChanges>(preview.Intent).Removal);
        Assert.False(preview.CanAuthorizePrune());
        var retained = Assert.Single(preview.Operations, item => item.Resource.ResourceId == "old-id");
        Assert.Equal(GitLifecycleAction.Retain, retained.Action);
        Assert.NotNull(retained.Expected);
        Assert.Equal(source.Id, retained.Expected.OwnerSourceId);
        Assert.Equal(
            GitLifecycleAction.Apply,
            Assert.Single(preview.Operations, item => item.Resource.Name == "next").Action);
        var scheduled = Assert.IsType<GitLifecycleScheduled>(
            (await Services.GetRequiredService<GitLifecyclePreviews>()
                .ConfirmAsync(
                    new ConfirmLifecyclePreview(
                        new GitSourceIdentity(source.TenantId, source.Id),
                        preview.Id,
                        preview.Revision,
                        Data.UserId),
                    default)).Value);
        await Worker.ProcessAsync(new GitSourceIdentity(source.TenantId, source.Id), default);
        var completed = (await Sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(completed.State).Completion);
        var old = await widgets.Documents.Find(item => item["_id"] == "old-id").SingleAsync();
        Assert.Equal(1, old["Revision"].ToInt64());
        Assert.Equal("value", old["Value"].AsString);
        var owner = await Inventory.Find(item => item.ResourceId == "old-id").SingleAsync();
        Assert.Equal(source.Id, owner.SourceId);
        Assert.True(owner.PendingRemoval);
        Assert.Equal(2, await widgets.Documents.CountDocumentsAsync(_ => true));
        var receipt = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == completed.Id && item.ResourceId == "old-id")
            .SingleAsync();
        Assert.Equal(GitLifecycleStep.Retain, receipt.Lifecycle!.Step);
        Assert.False(receipt.Changed);

        var requested = await Sources.RequestSyncAsync(
            new GitSourceCommand(
                source.TenantId,
                source.Id,
                source.Revision,
                "same-sha-after-retention",
                new GitRunInitiator(GitRunTrigger.Poll, null)),
            default);
        Assert.IsType<GitSourceScheduled>(requested.Value);
        await Worker.ProcessAsync(new GitSourceIdentity(source.TenantId, source.Id), default);
        Assert.Equal(
            GitRunCompletion.AlreadyCurrent,
            Assert.IsType<GitRunFinished>((await Latest(source)).State).Completion);
        Assert.Equal(1, widgets.ApplyCalls);
        Assert.Equal(2, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.True((await Inventory.Find(item => item.ResourceId == "old-id").SingleAsync()).PendingRemoval);
    }

    [Fact]
    public async Task Retain_confirmation_keeps_resource_preconditions_and_external_changes_make_it_stale()
    {
        var source = await Create(GitPrunePolicy.Retain);
        await Own(source, widgets, "old", "old-id");
        repository.Files = [Manifest("Widget", "next")];
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>(
            (await previews.CreateAsync(
                new CreateLifecyclePreview(
                    new GitSourceIdentity(source.TenantId, source.Id),
                    source.Revision,
                    new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Retain)),
                default)).Value);
        await widgets.Documents.UpdateOneAsync(_ => true, Builders<BsonDocument>.Update.Inc("Revision", 1));
        Assert.IsType<GitPreviewStale>(
            (await previews.ConfirmAsync(
                new ConfirmLifecyclePreview(
                    new GitSourceIdentity(source.TenantId, source.Id),
                    preview.Id,
                    preview.Revision,
                    Data.UserId),
                default)).Value);
        Assert.Equal(0, widgets.ApplyCalls);
        Assert.Equal(1, await widgets.Documents.CountDocumentsAsync(_ => true));
    }
}
