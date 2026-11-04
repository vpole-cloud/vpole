using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Outbox.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed class GitSourceLifecycleStoreTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private GitRepositorySources Sources => Services.GetRequiredService<GitRepositorySources>();
    private GitSourceLifecycleStore Lifecycle => Services.GetRequiredService<GitSourceLifecycleStore>();
    private GitRunStore Runs => Services.GetRequiredService<GitRunStore>();
    private IMongoCollection<RepositorySource> Documents =>
        Services.GetRequiredService<IMongoCollection<RepositorySource>>();

    [Fact]
    public async Task Pause_interrupts_detaching_source_and_retains_remaining_ownership()
    {
        var source = await Create();
        var detaching = source.BeginDetach();
        await Documents.ReplaceOneAsync(item => item.Id == source.Id, detaching);
        await Inventory.InsertOneAsync(Ownership(source));
        var paused = Assert.IsType<RepositorySource>((await Lifecycle.PauseAsync(Activity(detaching), default)).Value);
        Assert.Equal(GitSourceActivity.Paused, paused.Activity);
        Assert.Equal(detaching.Revision + 1, paused.Revision);
        var state = await Sources.StateAsync(source.TenantId, source.Id, default);
        Assert.Null(state.CurrentRun);
        Assert.Equal(state.RequestedGeneration, state.CompletedGeneration);
        Assert.Equal(1, await Inventory.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Pause_supersedes_live_claim_and_clears_pending_without_releasing_ownership()
    {
        var source = await Create();
        var claim = Assert.IsType<GitWorkClaim>((await Runs.ClaimAsync(new(source.TenantId, source.Id),
            "worker", default)).Value);
        await Inventory.InsertOneAsync(Ownership(source));
        await Sources.RequestSyncAsync(Command(source), default);
        var paused = Assert.IsType<RepositorySource>((await Lifecycle.PauseAsync(Activity(source), default)).Value);
        Assert.Equal(GitSourceActivity.Paused, paused.Activity);
        Assert.Equal(2, paused.Revision);
        var state = await Sources.StateAsync(source.TenantId, source.Id, default);
        Assert.Null(state.CurrentRun);
        Assert.Equal(state.RequestedGeneration, state.CompletedGeneration);
        Assert.True(state.Fence > claim.Handle.Stamp.Fence);
        Assert.Null(state.Pending.RetryOfRunId);
        Assert.Equal(1, await Inventory.CountDocumentsAsync(_ => true));
        Assert.False(await Runs.HeartbeatAsync(claim.Handle, default));
        Assert.False(await Runs.PinAsync(claim.Handle, new string('a', 40), default));
        var old = await Sources.RunAsync(source.TenantId, source.Id, claim.Run.Id, default);
        Assert.Equal(GitRunCompletion.Superseded, Assert.IsType<GitRunFinished>(old!.State).Completion);
        Assert.IsType<GitNoWorkAvailable>((await Runs.ClaimAsync(new(source.TenantId, source.Id),
            "stale-job", default)).Value);
        Assert.IsType<GitSourceInactive>((await Sources.RequestSyncAsync(Command(paused), default)).Value);
        Assert.IsType<GitSourceInactive>((await Sources.RetryAsync(new(Command(paused), claim.Run.Id), default)).Value);
    }

    [Fact]
    public async Task Resume_schedules_fresh_input_with_actor_audit_and_no_old_retry()
    {
        var source = await Create();
        var paused = Assert.IsType<RepositorySource>((await Lifecycle.PauseAsync(Activity(source), default)).Value);
        var resumed = Assert.IsType<RepositorySource>((await Lifecycle.ResumeAsync(Activity(paused), default)).Value);
        Assert.Equal(GitSourceActivity.Active, resumed.Activity);
        Assert.Equal(3, resumed.Revision);
        var state = await Sources.StateAsync(source.TenantId, source.Id, default);
        Assert.Equal(2, state.RequestedGeneration);
        Assert.Equal(1, state.CompletedGeneration);
        Assert.Null(state.Pending.RetryOfRunId);
        Assert.Equal(new GitRunInitiator(GitRunTrigger.Manual, TestData.UserId), state.Pending.Initiator);
        var claim = Assert.IsType<GitWorkClaim>((await Runs.ClaimAsync(new(source.TenantId, source.Id),
            "resumed-worker", default)).Value);
        Assert.IsType<GitInputUnresolved>(claim.Run.Input);
        Assert.Equal(3, claim.Run.Stamp.ConfigurationRevision);
        Assert.Equal(2, await WakeCount());
        Assert.IsType<GitSourceInactive>((await Lifecycle.ResumeAsync(Activity(resumed), default)).Value);
    }

    [Fact]
    public async Task Resume_outbox_failure_rolls_back_source_state_and_generation()
    {
        var source = await Create();
        var paused = Assert.IsType<RepositorySource>((await Lifecycle.PauseAsync(Activity(source), default)).Value);
        var states = Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>();
        var runs = Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>();
        var transactions = Services.GetRequiredService<MongoRunTransaction>();
        var failingSources = new GitRepositorySources(Documents, states, runs,
            Services.GetRequiredService<IMongoCollection<GitSourceRequestReceipt>>(), transactions,
            (_, _, _) => throw new InvalidOperationException("Injected resume outbox failure"));
        var failing = new GitSourceLifecycleStore(Documents, states, runs, Inventory, failingSources, transactions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ResumeAsync(Activity(paused), default));
        var persisted = (await Sources.LoadAsync(source.TenantId, source.Id, default))!;
        Assert.Equal(paused, persisted);
        var state = await Sources.StateAsync(source.TenantId, source.Id, default);
        Assert.Equal(1, state.RequestedGeneration);
        Assert.Equal(1, state.CompletedGeneration);
        Assert.Equal(1, await WakeCount());
    }

    [Fact]
    public async Task Paused_policy_change_keeps_work_stopped_and_exposes_explicit_prune()
    {
        var source = await Create();
        var paused = Assert.IsType<RepositorySource>((await Lifecycle.PauseAsync(Activity(source), default)).Value);
        var changed = Assert.IsType<RepositorySource>((await Sources.ChangePolicyAsync(new(source.TenantId,
            source.Id, paused.Revision, new(TimeSpan.FromMinutes(10)), GitPrunePolicy.Prune), default)).Value);
        Assert.Equal(GitSourceActivity.Paused, changed.Activity);
        Assert.Equal(GitPrunePolicy.Prune, changed.Prune);
        var state = await Sources.StateAsync(source.TenantId, source.Id, default);
        Assert.Equal(state.RequestedGeneration, state.CompletedGeneration);
        Assert.Equal(1, await WakeCount());
        var view = (await Services.GetRequiredService<GitRepositoryQueries>()
            .SourceAsync(source.TenantId, source.Id, default))!;
        Assert.Equal(GitSourceActivity.Paused, view.Activity);
        Assert.Equal(GitPrunePolicy.Prune, view.Prune);
    }

    [Fact]
    public async Task Ordinary_signals_and_recovery_do_not_schedule_paused_or_detached_sources()
    {
        var source = await Create();
        var paused = Assert.IsType<RepositorySource>((await Lifecycle.PauseAsync(Activity(source), default)).Value);
        var scheduler = Services.GetRequiredService<GitSourceScheduling>();
        var signal = new RepositorySyncSignal(source.TenantId, source.Repository.Provider,
            source.Repository.ConnectionId, source.Repository.ExternalRepositoryId, source.Repository.Branch, "push");
        await scheduler.RequestRepositorySyncAsync(signal, default);
        Assert.Empty(await scheduler.RecoverableSourcesAsync(default));
        var detached = paused.BeginDetach().CompleteDetach(0);
        await Documents.ReplaceOneAsync(item => item.Id == source.Id, detached);
        await scheduler.RequestRepositorySyncAsync(signal with { SignalId = "later-push" }, default);
        Assert.Empty(await scheduler.RecoverableSourcesAsync(default));
        Assert.Equal(1, (await Sources.StateAsync(source.TenantId, source.Id, default)).RequestedGeneration);
        Assert.Equal(1, await WakeCount());
    }

    [Fact]
    public async Task Paused_poll_candidates_do_not_starve_later_active_sources()
    {
        var inactive = await Create();
        await Lifecycle.PauseAsync(Activity(inactive), default);
        var active = await Create();
        var states = Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>();
        await states.UpdateOneAsync(item => item.Source.SourceId == inactive.Id,
            Builders<GitSynchronizationState>.Update.Set(item => item.NextPollAtUtc, DateTime.UtcNow.AddDays(-1)));
        var current = await Sources.StateAsync(active.TenantId, active.Id, default);
        await states.ReplaceOneAsync(item => item.Id == current.Id, current with
        {
            CompletedGeneration = current.RequestedGeneration,
            NextPollAtUtc = DateTime.UtcNow.AddMinutes(-1)
        });
        var options = Options.Create(new GitOpsWorkerOptions
        {
            BatchSize = 1
        });
        var scheduler = new GitSourceScheduling(Documents, states,
            Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>(),
            Services.GetRequiredService<IMongoCollection<GitSourceRequestReceipt>>(), Inventory,
            Services.GetRequiredService<IMongoCollection<GitAcceptedReference>>(),
            Services.GetRequiredService<MongoRunTransaction>(),
            Services.GetRequiredService<StoreOutboxWrites>(), TimeProvider.System, options);
        Assert.Equal(new GitSourceIdentity(active.TenantId, active.Id),
            Assert.Single(await scheduler.RecoverableSourcesAsync(default)));
        Assert.Equal(1, (await Sources.StateAsync(inactive.TenantId, inactive.Id, default)).RequestedGeneration);
    }

    [Fact]
    public async Task Delete_requires_empty_inventory_including_pending_removal_and_no_pending_work()
    {
        var source = await Create();
        Assert.IsType<GitSourceBusy>((await Lifecycle.DeleteAsync(new(source.TenantId, source.Id,
            source.Revision), default)).Value);
        var paused = Assert.IsType<RepositorySource>((await Lifecycle.PauseAsync(Activity(source), default)).Value);
        await Inventory.InsertOneAsync(Ownership(source) with { PendingRemoval = true });
        var blocked = Assert.IsType<GitSourceDetachRequired>((await Lifecycle.DeleteAsync(new(source.TenantId,
            source.Id, paused.Revision), default)).Value);
        Assert.Equal(1, blocked.OwnedResources);
        var retained = (await Sources.LoadAsync(source.TenantId, source.Id, default))!;
        Assert.Equal(GitSourceActivity.Paused, retained.Activity);
    }

    [Fact]
    public async Task Tombstone_hides_source_and_history_but_preserves_creation_idempotency()
    {
        var request = Creation();
        var source = Assert.IsType<RepositorySource>((await Sources.CreateAsync(request, default)).Value);
        var claim = Assert.IsType<GitWorkClaim>((await Runs.ClaimAsync(new(source.TenantId, source.Id),
            "worker", default)).Value);
        var paused = Assert.IsType<RepositorySource>((await Lifecycle.PauseAsync(Activity(source), default)).Value);
        var deletion = new DeleteGitSource(source.TenantId, source.Id, paused.Revision);
        Assert.IsType<GitSourceDeletionRecorded>((await Lifecycle.DeleteAsync(deletion, default)).Value);
        Assert.IsType<GitSourceDeletionRecorded>((await Lifecycle.DeleteAsync(deletion, default)).Value);
        Assert.Null(await Sources.LoadAsync(source.TenantId, source.Id, default));
        var queries = Services.GetRequiredService<GitRepositoryQueries>();
        Assert.Null(await queries.SourceAsync(source.TenantId, source.Id, default));
        Assert.Empty((await queries.ListAsync(source.TenantId, 1, 50, default)).Items);
        Assert.Null(await queries.RunsAsync(source.TenantId, source.Id, 1, 50, default));
        Assert.Null(await queries.RunAsync(source.TenantId, source.Id, claim.Run.Id, default));
        Assert.Null(await queries.OperationsAsync(source.TenantId, source.Id, claim.Run.Id, 1, 50, default));
        Assert.IsType<GitSourceCreationDeleted>((await Sources.CreateAsync(request, default)).Value);
        Assert.IsType<GitSourceNotFound>((await Sources.RequestSyncAsync(Command(paused), default)).Value);
        var tombstone = await Documents.Find(item => item.Id == source.Id).SingleAsync();
        Assert.Equal(GitSourceActivity.Deleted, tombstone.Activity);
        Assert.Equal(source.Audit.CreatedByUserId, tombstone.Audit.CreatedByUserId);
        Assert.InRange(tombstone.Audit.CreatedAtUtc, source.Audit.CreatedAtUtc.AddMilliseconds(-1),
            source.Audit.CreatedAtUtc);
        Assert.Equal(1, await Documents.CountDocumentsAsync(_ => true));
        Assert.NotNull(await Sources.RunAsync(source.TenantId, source.Id, claim.Run.Id, default));
    }

    [Fact]
    public async Task Generic_retry_cannot_reuse_consumed_lifecycle_approval_in_a_new_run()
    {
        var source = await Create();
        var claim = Assert.IsType<GitWorkClaim>((await Runs.ClaimAsync(new(source.TenantId, source.Id),
            "lifecycle-worker", default)).Value);
        var now = DateTime.UtcNow;
        var snapshot = new GitAcceptedSnapshot(new string('a', 40), "desired", "pole.io/v1alpha1", [], []);
        var preview = GitOperationPreviewFactory.Create("preview",
            new(new(source.TenantId, source.Id), source.Revision, 0,
                new GitPreviewRepositoryInput(source.Repository, snapshot)),
            new GitPreviewRelocateSource(source.Repository with { ManifestDirectory = "next" }), [], now);
        preview = preview.Confirm(new(preview.Id, preview.Revision, preview.Source, [], TestData.UserId,
            now, claim.Run.Id));
        var finished = claim.Run with
        {
            Input = new GitInputLifecycle(GitLifecycleExecutionPlanFactory.Create(preview)),
            State = new GitRunFinished(GitRunCompletion.Blocked, now)
        };
        Assert.NotNull(finished.Commit);
        await Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>()
            .ReplaceOneAsync(item => item.Id == finished.Id, finished);
        var states = Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>();
        var current = await Sources.StateAsync(source.TenantId, source.Id, default);
        await states.ReplaceOneAsync(item => item.Id == current.Id,
            current.CompleteRun(finished.Stamp, GitRunCompletion.Blocked));

        var retried = await Sources.RetryAsync(new(Command(source), finished.Id), default);
        Assert.IsType<GitRetryNotAvailable>(retried.Value);
        Assert.Equal(1, (await Sources.StateAsync(source.TenantId, source.Id, default)).RequestedGeneration);
        Assert.Equal(1, await Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>()
            .CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Activity_commands_enforce_tenant_and_revision()
    {
        var source = await Create();
        Assert.IsType<GitSourceNotFound>((await Lifecycle.PauseAsync(Activity(source) with
        { TenantId = "foreign" }, default)).Value);
        Assert.IsType<GitSourceRevisionConflict>((await Lifecycle.PauseAsync(Activity(source) with
        { ExpectedRevision = 99 }, default)).Value);
        Assert.IsType<GitSourceNotFound>((await Lifecycle.DeleteAsync(new("foreign", source.Id, 1), default)).Value);
        var unchanged = (await Sources.LoadAsync(source.TenantId, source.Id, default))!;
        Assert.Equal(GitSourceActivity.Active, unchanged.Activity);
    }

    private IMongoCollection<ManagedDeclaration> Inventory =>
        Services.GetRequiredService<IMongoCollection<ManagedDeclaration>>();

    private async Task<RepositorySource> Create() =>
        Assert.IsType<RepositorySource>((await Sources.CreateAsync(Creation(), default)).Value);

    private static CreateGitRepositorySource Creation() => new(TestData.TenantId, Guid.NewGuid().ToString("D"),
        new("Example", "connection", "repository", "main", ""), new(TimeSpan.FromMinutes(5)),
        new(TestData.UserId, DateTime.UtcNow), "https://example.test/repository");

    private static ChangeGitSourceActivity Activity(RepositorySource source) =>
        new(source.TenantId, source.Id, source.Revision, TestData.UserId);

    private static GitSourceCommand Command(RepositorySource source) =>
        new(source.TenantId, source.Id, source.Revision, Guid.NewGuid().ToString("D"),
            new(GitRunTrigger.Manual, TestData.UserId));

    private static ManagedDeclaration Ownership(RepositorySource source) => new("ownership", source.TenantId,
        "Widget", "demo", source.Id, source.DisplayUrl, "resource", "desired", null, null, false, "run", "Prepare", 0,
        GitResourceMaterialization.Created, GitManagedProjectionsFactory.Empty());

    private Task<long> WakeCount() => DbContext.OutboxMessages.CountDocumentsAsync(item =>
        item.EventType == "pole.gitops/v1/source-work-requested");
}
