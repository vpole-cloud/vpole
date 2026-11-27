// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class GitPrunePlanningTests
{
    [Fact]
    public async Task Retain_requires_confirmation_for_same_kind_creation_and_pending_removal_before_mutations()
    {
        var source = await Create(GitPrunePolicy.Retain);
        await Own(source, widgets, "old", "old-id");
        repository.Files = [Manifest("Widget", "new")];
        await Worker.ProcessAsync(new GitSourceIdentity(source.TenantId, source.Id), default);

        var run = await Latest(source);
        Assert.Equal(0, widgets.ApplyCalls);
        Assert.Equal(1, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.Equal("old-id", (await widgets.Documents.Find(_ => true).SingleAsync())["_id"].AsString);
        var waiting = Assert.IsType<GitInputAwaitingApproval>(run.Input);
        Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
        var preview = await Services.GetRequiredService<IMongoCollection<GitOperationPreview>>()
            .Find(item => item.Id == waiting.PreviewId)
            .SingleAsync();
        Assert.IsType<GitPreviewAwaitingConfirmation>(preview.State);
        Assert.False(preview.CanAuthorizePrune());
        Assert.True((await Inventory.Find(item => item.ResourceId == "old-id").SingleAsync()).PendingRemoval);
        Assert.Equal(repository.Commit, waiting.Snapshot.Commit);
        Assert.Equal(1, repository.ReadCalls);
    }

    [Fact]
    public async Task Retain_allows_existing_same_kind_and_unrelated_creation_without_approval()
    {
        var source = await Create(GitPrunePolicy.Retain);
        await Own(source, widgets, "old", "old-id");
        await Own(source, widgets, "kept", "kept-id");
        repository.Files = [Manifest("Widget", "kept"), Manifest("Gadget", "new")];
        await Worker.ProcessAsync(new GitSourceIdentity(source.TenantId, source.Id), default);

        var run = await Latest(source);
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.IsType<GitInputAccepted>(run.Input);
        Assert.Equal(0, widgets.ApplyCalls);
        Assert.Equal(1, gadgets.ApplyCalls);
        Assert.Equal(2, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.Equal(1, await gadgets.Documents.CountDocumentsAsync(_ => true));
        Assert.True((await Inventory.Find(item => item.ResourceId == "old-id").SingleAsync()).PendingRemoval);
        Assert.Equal(
            0,
            await Services.GetRequiredService<IMongoCollection<GitOperationPreview>>().CountDocumentsAsync(_ => true));
    }
}
