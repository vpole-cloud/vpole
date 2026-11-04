using Pole.Features.GitOps.Primitives;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class RepositorySourceDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
    private static readonly GitSourceIdentity Identity = new("tenant", "source");

    private static readonly GitRepositoryLocation Repository =
        new("Example", "connection", "repository", "main", "deploy");

    private static readonly GitPendingRequest Pending = new(new(GitRunTrigger.Poll, null), null);
    private static readonly string Commit = new('a', 40);

    [Fact]
    public void Source_factory_sets_explicit_defaults_and_pause_retains_identity()
    {
        var source = RepositorySourceFactory.Create(Identity,
            Repository,
            new(TimeSpan.FromMinutes(5)),
            new("creator", Now),
            "creation",
            "hash",
            "https://example.test/repository");

        Assert.Equal(1, source.Revision);
        Assert.Equal(GitPrunePolicy.Retain, source.Prune);
        Assert.Equal(GitSourceActivity.Active, source.Activity);
        Assert.Equal("deploy", source.Repository.ManifestDirectory);
        var paused = source.Pause();
        Assert.Equal(source.Id, paused.Id);
        Assert.Equal(source.Repository, paused.Repository);
        Assert.Equal(2, paused.Revision);
        Assert.Same(paused, paused.Pause());
        Assert.Equal(3, paused.Resume().Revision);
        Assert.Equal(SourceDeletionEligibility.DetachRequired, source.EvaluateDeletion(1));
        Assert.Equal(SourceDeletionEligibility.Allowed, source.EvaluateDeletion(0));
        RoundTrip(source);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("a/../b")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("C:/path")]
    public void Source_factory_rejects_unsafe_directory(string directory) =>
        Assert.Throws<ArgumentException>(() => RepositorySourceFactory.Create(Identity,
            Repository with { ManifestDirectory = directory },
            new(TimeSpan.FromMinutes(5)),
            new("creator", Now),
            "creation",
            "hash",
            "https://example.test/repository"));

    [Fact]
    public void Push_during_run_remains_pending_and_old_run_cannot_complete_it()
    {
        var state = GitSynchronizationStateFactory.Create(Identity, Now);
        Assert.Equal(new GitCommitProgress(null, null, null), state.Commits);
        Assert.Throws<InvalidOperationException>(() => state.BeginRun("run"));
        state = state.RequestSync(Pending).Observe("sha").BeginRun("run").Attempt("sha");
        var stamp = new GitRunStamp(Identity, "run", 1, 1, 1);
        state = state.RequestSync(Pending).Observe("new-sha");
        Assert.Throws<InvalidOperationException>(() => state.CompleteRun(stamp with { Generation = 2 },
            GitRunCompletion.ConfigurationApplied));
        Assert.Throws<InvalidOperationException>(() => state.CompleteRun(stamp with { Fence = 2 },
            GitRunCompletion.ConfigurationApplied));
        Assert.Throws<InvalidOperationException>(() => state.CompleteRun(stamp with
        {
            Source = new("other-tenant", "source")
        },
            GitRunCompletion.ConfigurationApplied));

        var completed = state.CompleteRun(stamp, GitRunCompletion.ConfigurationApplied);
        Assert.Equal(1, completed.CompletedGeneration);
        Assert.Equal(2, completed.RequestedGeneration);
        Assert.Equal(new GitCommitProgress("new-sha", "sha", "sha"), completed.Commits);
        var retry = completed.BeginRun("next");
        Assert.Equal(2, retry.Fence);
        RoundTrip(retry);
    }

    [Fact]
    public void Blocked_run_does_not_advance_applied_commit()
    {
        var state = GitSynchronizationStateFactory.Create(Identity, Now)
            .RequestSync(Pending).BeginRun("run").Attempt("sha");
        var completed = state.CompleteRun(new(Identity, "run", 1, 1, 1), GitRunCompletion.Blocked);
        Assert.Null(completed.Commits.FullyApplied);
        Assert.Equal("sha", completed.Commits.Attempted);
    }

    [Fact]
    public void Claim_expiry_and_attempt_fence_reject_stale_completion()
    {
        var run = NewRun().Pin(Commit).Accept(new(Commit, "hash", "pole.io/v1alpha1", [], []));
        RoundTrip(run);
        var first = new GitRunLease("worker-a", 1, Now.AddMinutes(1));
        run = run.Claim(first, Now);
        RoundTrip(run);
        var second = new GitRunLease("worker-b", 2, Now.AddMinutes(3));
        Assert.Throws<InvalidOperationException>(() => run.Claim(second, Now));
        Assert.Throws<InvalidOperationException>(() => run.Complete(
            new(first.WorkerId, first.Attempt),
            GitRunCompletion.ConfigurationApplied,
            Now.AddMinutes(1)));
        run = run.Claim(second, Now.AddMinutes(2));
        Assert.Throws<InvalidOperationException>(() => run.Complete(
            new(first.WorkerId, first.Attempt),
            GitRunCompletion.ConfigurationApplied,
            Now.AddMinutes(2)));
        run = run.Complete(
            new(second.WorkerId, second.Attempt),
            GitRunCompletion.ConfigurationApplied,
            Now.AddMinutes(2));
        RoundTrip(run);
        Assert.Throws<InvalidOperationException>(() => run.Claim(
            second with { ExpiresAtUtc = Now.AddMinutes(5) },
            Now.AddMinutes(4)));
    }

    [Fact]
    public void Fetch_failure_can_finish_without_an_accepted_snapshot()
    {
        var run = NewRun();
        Assert.Null(run.Commit);
        RoundTrip(run);
        var lease = new GitRunLease("worker", 1, Now.AddMinutes(2));
        run = run.Claim(lease, Now);
        Assert.Throws<InvalidOperationException>(() => run.Complete(new("worker", 1),
            GitRunCompletion.ConfigurationApplied,
            Now));
        run = run.Complete(new("worker", 1), GitRunCompletion.Failed, Now) with
        {
            Diagnostics = [new("RepositoryNotFound", null, null, null)]
        };
        Assert.IsType<GitInputUnresolved>(run.Input);
        RoundTrip(run);
    }

    [Fact]
    public void Download_retry_retains_commit_and_needs_due_time_and_next_attempt()
    {
        var run = NewRun().Claim(new("worker", 1, Now.AddMinutes(2)), Now).Pin(Commit);
        Assert.Throws<InvalidOperationException>(() => run.Pin(new string('b', 40)));
        run = run.Delay(new("worker", 1), Now, Now.AddMinutes(1), new("Unavailable", null, null, null));
        RoundTrip(run);
        Assert.Throws<InvalidOperationException>(() => run.Claim(new("next", 2, Now.AddMinutes(2)), Now));
        run = run.Claim(new("next", 2, Now.AddMinutes(3)), Now.AddMinutes(1));
        Assert.Equal(Commit, run.Commit);
        Assert.Equal(1, run.ConsecutiveFailures);
        RoundTrip(run);
    }

    [Fact]
    public void Heartbeat_does_not_change_claim_identity()
    {
        var run = NewRun().Claim(new("worker", 1, Now.AddMinutes(1)), Now);
        run = run with { State = new GitRunClaimed(new("worker", 1, Now.AddMinutes(3))) };
        run = run.Complete(new("worker", 1), GitRunCompletion.Failed, Now.AddMinutes(2));
        Assert.IsType<GitRunFinished>(run.State);
    }

    private static GitReconciliationRun NewRun() => GitReconciliationRunFactory.Create(
        new(Identity, "run", 1, 1, 1),
        Repository,
        new(GitRunTrigger.Poll, null),
        Now,
        new GitInputUnresolved(),
        null);

    [Fact]
    public void Operation_and_inventory_current_schema_round_trip_without_feature_registration()
    {
        var resource = new GitResourceIdentity("Widget", "demo", "id");
        var expected = new GitResourcePreconditions(resource, "source", [new("Widget/id", 4)], "hash");
        var operation = new GitPlannedOperation("operation",
            new(Identity, "run", 1, 1, 1),
            resource,
            GitPlannedAction.Update,
            DeclarationPhase.Configure,
            expected,
            "desired",
            new GitOperationPending());
        RoundTrip(operation);
        RoundTrip(operation with { State = new GitOperationSucceeded() });
        RoundTrip(operation with { State = new GitOperationBlocked(new("spec", "Unavailable")) });
        var inventory = new GitManagedResourceState("inventory",
            Identity,
            resource,
            new("desired", "{}"),
            null,
            false).RetainRemoved();
        Assert.Equal(Identity, inventory.Owner);
        Assert.True(inventory.PendingRemoval);
        RoundTrip(inventory);
    }

    private static void RoundTrip<T>(T value) where T : notnull
    {
        var bson = value.ToBsonDocument();
        Assert.True(bson.Contains("_id"));
        Assert.Equal(bson, BsonSerializer.Deserialize<T>(bson).ToBsonDocument());
    }
}
