using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_cleanup_status_proves_its_own_revision_but_never_an_external_gap(bool external)
    {
        var final = await ApplyDeferredConnectionCleanupAsync(external);
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == final.Id).ToListAsync();
        Assert.Equal(!external, receipts.Any(item => item.Lifecycle?.Step == GitLifecycleStep.RemovalCompleted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_workload_apply_advances_own_group_fences_but_rejects_external_changes(bool external)
    {
        var intercepted = false;
        var mutations = new BeforeNoOp(Services.GetRequiredService<IGitOpsMutations>(), async (operation, ct) =>
        {
            if (intercepted || operation.Declaration.Address.Kind != "WebService" ||
                operation.Phase != DeclarationPhase.Configure)
                return;

            intercepted = true;
            if (external)
                await DbContext.ResourceGroups.UpdateOneAsync(item => item.TenantId == TestData.TenantId,
                    Builders<ResourceGroup>.Update.Inc(item => item.RegistrationFence, 1), cancellationToken: ct);
        });
        var snapshots = ActivatorUtilities.CreateInstance<GitAcceptedSnapshotExecutor>(Services, mutations);
        var lifecycle = ActivatorUtilities.CreateInstance<GitLifecycleExecutor>(Services, snapshots);
        var worker = ActivatorUtilities.CreateInstance<RepositorySourceJob>(Services, snapshots, lifecycle);
        var final = await ApplyDeferredConnectionCleanupAsync(false, worker);
        Assert.True(intercepted);
        Assert.Equal(external ? GitRunCompletion.Blocked : GitRunCompletion.ConfigurationApplied,
            Assert.IsType<GitRunFinished>(final.State).Completion);
        if (external)
            Assert.Contains(final.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
        else
            Assert.Empty(final.Diagnostics);
    }

    private async Task<GitReconciliationRun> ApplyDeferredConnectionCleanupAsync(bool external,
        RepositorySourceJob? worker = null)
    {
        var files = ConnectionSnapshot();
        await Apply(files);
        var sources = Services.GetRequiredService<GitRepositorySources>();
        var owner = (await sources.LoadAsync(TestData.TenantId, source, default))!;
        var retained = files[0].Content.Split("\n---\n").Select(item => JsonNode.Parse(item)!)
            .Where(item => item["kind"]!.GetValue<string>() != "ResourceConnection").ToArray();
        repository.Publish(owner.Repository.ExternalRepositoryId,
            [new ManifestFile("retained.yaml", string.Join("\n---\n", retained.Select(item => item.ToJsonString())))],
            "remove-connection");
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(
            new(new(owner.TenantId, owner.Id), owner.Revision,
                new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default)).Value);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(
            new(new(owner.TenantId, owner.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(owner.TenantId, owner.Id), default);
        var connection = await Connections.Find(_ => true).SingleAsync();
        var deleting = Assert.IsType<DeletingResourceConnection>(connection.Lifecycle);
        if (external)
            await Connections.UpdateOneAsync(item => item.Id == connection.Id,
                Builders<ResourceConnection>.Update.Inc(item => item.Revision, 1));

        // Hold only the separate purge callback to observe the committed status-before-purge window.
        using (var delayedPurge = TestServiceProviderFactory.Create(Fixture, services =>
                   services.AddScoped<PurgeResourceConnectionIfComplete>(_ => (_, _) => Task.CompletedTask)))
        using (var scope = delayedPurge.CreateScope())
        {
            foreach (var target in deleting.Targets)
                await scope.ServiceProvider.GetRequiredService<RecordResourceConnectionCleanup>()(connection.TenantId,
                    connection.Id, target.Snapshot.PolicyLocation, connection.Generation, null);
        }

        var completed = await Connections.Find(item => item.Id == connection.Id).SingleAsync();
        Assert.True(completed.IsCleanupComplete);
        var proofs = await Services.GetRequiredService<IMongoCollection<GitLifecycleCleanupProof>>()
            .Find(item => item.RunId == scheduled.RunId).ToListAsync();
        Assert.NotEmpty(proofs);
        Assert.All(proofs, proof => Assert.Equal(connection.Id, proof.Subject.ResourceId));
        Assert.Contains(proofs.SelectMany(item => item.Transitions), item =>
            item.DocumentId == ResourceConnectionPlanningVersion.RevisionDocumentId(connection.Id) &&
            item.Before == connection.Revision + (external ? 1 : 0));
        var pending = (await sources.RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
        await ResumeConnectionLifecycleAsync(pending);
        var advanced = (await sources.RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
        if (external)
        {
            Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(advanced.State).Completion);
            Assert.Contains(advanced.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
            Assert.True(await Inventory.Find(item => item.ResourceId == connection.Id).AnyAsync());
            return advanced;
        }

        Assert.IsType<GitRunWaitingCleanup>(advanced.State);
        Assert.Equal(0, advanced.ConsecutiveFailures);
        await Services.GetRequiredService<PurgeResourceConnectionIfComplete>()(completed);
        if (worker is null)
            await ResumeConnectionLifecycleAsync(advanced);
        else
        {
            var waiting = Assert.IsType<GitRunWaitingCleanup>(advanced.State);
            await Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>().ReplaceOneAsync(
                item => item.Id == advanced.Id, advanced with
                { State = new GitRunWaitingCleanup(waiting.LastAttempt, DateTime.UtcNow.AddMinutes(-1)) });
            await worker.ProcessAsync(new(owner.TenantId, owner.Id), default);
        }

        var final = (await sources.RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
        Assert.False(await Inventory.Find(item => item.ResourceId == connection.Id).AnyAsync());
        Assert.Equal(0, await Connections.CountDocumentsAsync(_ => true));
        return final;
    }
}
