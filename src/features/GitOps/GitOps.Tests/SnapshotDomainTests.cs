using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class SnapshotDomainTests
{
    private static DeclarationOwnership Ownership() =>
        new("tenant", "source", "Widget", "demo", "resource", "desired", "working", false);

    [Fact]
    public void Ownership_requires_tenant_source_and_the_accepted_resource_identity()
    {
        Assert.True(Ownership().CanBeMutatedBy(new("tenant", "source"), "resource"));
        Assert.False(Ownership().CanBeMutatedBy(new("foreign", "source"), "resource"));
        Assert.False(Ownership().CanBeMutatedBy(new("tenant", "other"), "resource"));
        Assert.False(Ownership().CanBeMutatedBy(new("tenant", "source"), "replacement"));
    }

    [Theory]
    [InlineData(GitRunCompletion.Blocked)]
    [InlineData(GitRunCompletion.Failed)]
    [InlineData(GitRunCompletion.Superseded)]
    public void Unsuccessful_runs_preserve_the_fully_applied_commit(GitRunCompletion completion)
    {
        var stamp = new GitRunStamp(new("tenant", "source"), "run", 1, 1, 1);
        var state = GitSynchronizationStateFactory.Create(stamp.Source, DateTime.UtcNow)
                .RequestSync(new(new(GitRunTrigger.Poll, null), null)).BeginRun(stamp.RunId) with
        {
            Commits = new("new", "new", "working")
        };
        var completed = state.CompleteRun(stamp, completion);
        Assert.Equal(new("new", "new", "working"), completed.Commits);
        Assert.Equal(1, completed.CompletedGeneration);
        Assert.Equal("new", state.CompleteRun(stamp, GitRunCompletion.ConfigurationApplied).Commits.FullyApplied);
        Assert.Throws<InvalidOperationException>(() => state.CompleteRun(stamp with { Fence = 2 }, completion));
    }

    [Fact]
    public void Removal_retains_identity_and_desired_observation_can_resume_it()
    {
        var owner = Ownership();
        var retained = owner.RetainPendingRemoval();
        Assert.Equal(owner.ResourceId, retained.ResourceId);
        Assert.Equal(owner.SourceId, retained.SourceId);
        Assert.True(retained.PendingRemoval);
        Assert.False(retained.ConfigurationApplied(false));
        var resumed = retained.ObserveDesired("next");
        Assert.False(resumed.PendingRemoval);
        Assert.Equal("next", resumed.DesiredHash);
        Assert.Equal("working", resumed.AppliedHash);
    }

    [Fact]
    public void Only_a_successful_final_phase_changes_the_applied_hash()
    {
        Assert.Null(new DeclarationCompletion("next", false, true).AppliedHashUpdate);
        Assert.Null(new DeclarationCompletion("next", true, false).AppliedHashUpdate);
        Assert.Equal("next", new DeclarationCompletion("next", true, true).AppliedHashUpdate);
        var applied = Ownership() with { AppliedHash = "desired" };
        Assert.True(applied.ConfigurationApplied(false));
        Assert.False(applied.ConfigurationApplied(true));
    }

    [Fact]
    public void Acceptance_rejects_duplicate_resources_and_operations_with_rebound_ids()
    {
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var run = GitReconciliationRunFactory.Create(new(new("tenant", "source"), "run", 1, 1, 1),
            new("Example", "connection", "repository", "main", ""),
            new(GitRunTrigger.Poll, null),
            DateTime.UtcNow,
            new GitInputUnresolved(),
            null).Pin(commit);
        var resource = new AcceptedDeclaration("Widget", "demo", "hash", "{}", "resource");
        var snapshot = new GitAcceptedSnapshot(commit,
            "desired",
            SnapshotSchema.ApiVersion,
            [resource],
            [new("Widget", "demo", "resource", DeclarationPhase.Prepare)]);
        Assert.IsType<GitInputAccepted>(run.Accept(snapshot).Input);
        Assert.Throws<InvalidOperationException>(() => run.Accept(snapshot with { Resources = [resource, resource] }));
        Assert.Throws<InvalidOperationException>(() => run.Accept(snapshot with
        {
            Operations = [new("Widget", "demo", "replacement", DeclarationPhase.Prepare)]
        }));
        Assert.Throws<InvalidOperationException>(() => run.Accept(snapshot with { Operations = [] }));
        Assert.Throws<ArgumentException>(() => run.Accept(snapshot with
        {
            Resources = [resource with { Hash = "" }]
        }));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 5)]
    public void Temporary_failures_use_the_bounded_backoff(int failure, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), new GitOpsWorkerOptions().RetryDelay(failure));
}
