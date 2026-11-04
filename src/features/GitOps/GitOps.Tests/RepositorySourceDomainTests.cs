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

    [Fact]
    public void Source_factory_sets_explicit_defaults_and_pause_retains_identity()
    {
        var source = RepositorySourceFactory.Create(Identity, new("connection", "repository", "main", "deploy/"),
            new(TimeSpan.FromMinutes(5)), new("creator", Now));

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
    public void Source_factory_rejects_unsafe_directory(string directory) =>
        Assert.Throws<ArgumentException>(() => RepositorySourceFactory.Create(Identity,
            new("connection", "repository", "main", directory), new(TimeSpan.FromMinutes(5)), new("creator", Now)));

    [Fact]
    public void Push_during_run_remains_pending_and_old_run_cannot_complete_it()
    {
        var state = GitSynchronizationStateFactory.Create(Identity);
        Assert.Equal(new GitCommitProgress(null, null, null), state.Commits);
        Assert.Throws<InvalidOperationException>(() => state.BeginRun("run", "sha"));
        state = state.RequestSync().Observe("sha").BeginRun("run", "sha");
        var stamp = new GitRunStamp(Identity, "run", 1, 1, 1);
        state = state.RequestSync().Observe("new-sha");
        Assert.Throws<InvalidOperationException>(() => state.CompleteRun(stamp with { Generation = 2 },
            GitRunCompletion.ConfigurationApplied));
        Assert.Throws<InvalidOperationException>(() => state.CompleteRun(stamp with { Fence = 2 },
            GitRunCompletion.ConfigurationApplied));
        Assert.Throws<InvalidOperationException>(() => state.CompleteRun(stamp with
        {
            Source = new("other-tenant", "source")
        }, GitRunCompletion.ConfigurationApplied));

        var completed = state.CompleteRun(stamp, GitRunCompletion.ConfigurationApplied);
        Assert.Equal(1, completed.CompletedGeneration);
        Assert.Equal(2, completed.RequestedGeneration);
        Assert.Equal(new GitCommitProgress("new-sha", "sha", "sha"), completed.Commits);
        var retry = completed.BeginRun("next", "new-sha");
        Assert.Equal(2, retry.Fence);
        RoundTrip(retry);
    }

    [Fact]
    public void Blocked_run_does_not_advance_applied_commit()
    {
        var state = GitSynchronizationStateFactory.Create(Identity).RequestSync().BeginRun("run", "sha");
        var completed = state.CompleteRun(new(Identity, "run", 1, 1, 1), GitRunCompletion.Blocked);
        Assert.Null(completed.Commits.FullyApplied);
        Assert.Equal("sha", completed.Commits.Attempted);
    }

    [Fact]
    public void Claim_expiry_and_attempt_fence_reject_stale_completion()
    {
        var run = GitReconciliationRunFactory.Create(new(Identity, "run", 1, 1, 1), new("sha", "hash", []),
            new(GitRunTrigger.Poll, null));
        RoundTrip(run);
        var first = new GitRunLease("worker-a", 1, Now.AddMinutes(1));
        run = run.Claim(first, Now);
        RoundTrip(run);
        var second = new GitRunLease("worker-b", 2, Now.AddMinutes(3));
        Assert.Throws<InvalidOperationException>(() => run.Claim(second, Now));
        Assert.Throws<InvalidOperationException>(() => run.Complete(first, GitRunCompletion.ConfigurationApplied,
            Now.AddMinutes(1)));
        run = run.Claim(second, Now.AddMinutes(2));
        Assert.Throws<InvalidOperationException>(() => run.Complete(first, GitRunCompletion.ConfigurationApplied,
            Now.AddMinutes(2)));
        run = run.Complete(second, GitRunCompletion.ConfigurationApplied, Now.AddMinutes(2));
        RoundTrip(run);
        Assert.Throws<InvalidOperationException>(() => run.Claim(
            second with { ExpiresAtUtc = Now.AddMinutes(5) }, Now.AddMinutes(4)));
    }

    [Fact]
    public void Operation_and_inventory_current_schema_round_trip_without_feature_registration()
    {
        var resource = new GitResourceIdentity("Widget", "demo", "id");
        var expected = new GitResourcePreconditions(resource, "source", [new("Widget/id", 4)], "hash");
        var operation = new GitPlannedOperation("operation", new(Identity, "run", 1, 1, 1), resource,
            GitPlannedAction.Update, DeclarationPhase.Configure, expected, "desired", new GitOperationPending());
        RoundTrip(operation);
        RoundTrip(operation with { State = new GitOperationSucceeded() });
        RoundTrip(operation with { State = new GitOperationBlocked(new("spec", "Unavailable")) });
        var inventory = new GitManagedResourceState("inventory", Identity, resource,
            new("desired", "{}"), null, false).RetainRemoved();
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
