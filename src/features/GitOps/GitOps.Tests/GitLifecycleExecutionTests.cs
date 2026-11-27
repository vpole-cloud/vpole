// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitLifecycleExecutionTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    private static readonly GitSourceIdentity Source = new("tenant", "source");
    private static readonly GitRepositoryLocation Repository = new("GitHub", "connection", "repository", "main", "");

    [Fact]
    public void Unconfirmed_preview_cannot_authorize_an_execution()
    {
        Assert.Throws<InvalidOperationException>(() => GitLifecycleExecutionPlanFactory.Create(Preview()));
    }

    [Fact]
    public void Offline_detach_is_claimed_and_completed_without_a_fabricated_commit()
    {
        var preview = Preview();
        var confirmed = preview.Confirm(
            new GitPreviewConfirmationContext(preview.Id, preview.Revision, preview.Source, [], "actor", Now, "run"));
        var plan = GitLifecycleExecutionPlanFactory.Create(confirmed);
        var run = GitReconciliationRunFactory
            .Create(
                new GitRunStamp(Source, "run", 1, 1, 1),
                Repository,
                new GitRunInitiator(GitRunTrigger.Manual, "actor"),
                Now,
                new GitInputLifecycle(plan),
                null)
            .Claim(new GitRunLease("worker", 1, Now.AddMinutes(2)), Now);

        Assert.Null(run.Commit);
        Assert.Null(run.AcceptedSnapshot);
        Assert.Throws<InvalidOperationException>(() => run.Complete(
            new GitClaimIdentity("worker", 1),
            GitRunCompletion.ConfigurationApplied,
            Now));
        var completed = run.Complete(new GitClaimIdentity("worker", 1), GitRunCompletion.LifecycleCompleted, Now);
        Assert.Equal(GitRunCompletion.LifecycleCompleted, Assert.IsType<GitRunFinished>(completed.State).Completion);
        var bson = completed.ToBsonDocument();
        Assert.Equal(bson, BsonSerializer.Deserialize<GitReconciliationRun>(bson).ToBsonDocument());
    }

    [Fact]
    public void Repository_run_cannot_report_lifecycle_completion()
    {
        var run = GitReconciliationRunFactory
            .Create(
                new GitRunStamp(Source, "run", 1, 1, 1),
                Repository,
                new GitRunInitiator(GitRunTrigger.Poll, null),
                Now,
                new GitInputUnresolved(),
                null)
            .Claim(new GitRunLease("worker", 1, Now.AddMinutes(2)), Now);
        Assert.Throws<InvalidOperationException>(() => run.Complete(
            new GitClaimIdentity("worker", 1),
            GitRunCompletion.LifecycleCompleted,
            Now));
    }

    [Fact]
    public void Cleanup_wait_preserves_failure_budget_and_reclaims_monotonically()
    {
        var preview = Preview();
        var approved = preview.Confirm(
            new GitPreviewConfirmationContext(preview.Id, 1, preview.Source, [], "actor", Now, "run"));
        var run = GitReconciliationRunFactory
            .Create(
                new GitRunStamp(Source, "run", 1, 1, 1),
                Repository,
                new GitRunInitiator(GitRunTrigger.Manual, "actor"),
                Now,
                new GitInputLifecycle(GitLifecycleExecutionPlanFactory.Create(approved)),
                null)
            .Claim(new GitRunLease("first", 1, Now.AddMinutes(2)), Now)
            .WaitCleanup(new GitClaimIdentity("first", 1), Now, Now.AddSeconds(20));
        Assert.Equal(0, run.ConsecutiveFailures);
        Assert.IsType<GitRunWaitingCleanup>(run.State);
        Assert.Throws<InvalidOperationException>(() => run.Claim(new GitRunLease("early", 2, Now.AddMinutes(3)), Now));
        var reclaimed = run.Claim(new GitRunLease("second", 2, Now.AddMinutes(3)), Now.AddSeconds(21));
        Assert.Equal(2, Assert.IsType<GitRunClaimed>(reclaimed.State).Lease.Attempt);
        var bson = run.ToBsonDocument();
        Assert.Equal(bson, BsonSerializer.Deserialize<GitReconciliationRun>(bson).ToBsonDocument());
    }

    private static GitOperationPreview Preview() => GitOperationPreviewFactory.Create(
        "preview",
        new GitPreviewSourceStamp(Source, 1, 0, new GitPreviewInventoryInput()),
        new GitPreviewDetachSource(),
        [],
        Now);
}
