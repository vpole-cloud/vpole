using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class SnapshotDomainTests
{
    private static readonly ExecutionStamp Stamp = new("tenant", "source", "run", 3, 2);
    private static readonly AcceptedResourceBinding Binding = new("Widget", "demo", "resource", "desired");
    private static SnapshotExecution Execution() => SnapshotExecutionFactory.Create(
        Stamp, "https://example.test/source", "new-commit", "working-commit", [Binding], new ApplyingSnapshot());
    private static DeclarationOwnership Ownership() => new("tenant", "source", "Widget", "demo", "resource", "desired", "working", false);

    [Fact]
    public void Acceptance_rejects_stale_fences_and_implicit_source_relocation()
    {
        var current = Execution();
        Assert.Equal(SnapshotAcceptance.Accepted, current.EvaluateAcceptance(3, current.SourceUrl));
        Assert.Equal(SnapshotAcceptance.StaleExecution, current.EvaluateAcceptance(2, current.SourceUrl));
        Assert.Equal(SnapshotAcceptance.SourceRelocationRequired, current.EvaluateAcceptance(3, "https://example.test/other"));
    }

    [Fact]
    public void Every_execution_identity_component_and_accepted_binding_is_required()
    {
        var current = Execution();
        Assert.True(current.AllowsOperation(Stamp, Binding));
        Assert.False(current.AllowsOperation(Stamp with { TenantId = "other" }, Binding));
        Assert.False(current.AllowsOperation(Stamp with { SourceId = "other" }, Binding));
        Assert.False(current.AllowsOperation(Stamp with { RunId = "other" }, Binding));
        Assert.False(current.AllowsOperation(Stamp with { Fence = 2 }, Binding));
        Assert.False(current.AllowsOperation(Stamp with { Attempt = 1 }, Binding));
        Assert.False(current.AllowsOperation(Stamp, Binding with { Kind = "Other" }));
        Assert.False(current.AllowsOperation(Stamp, Binding with { Name = "renamed" }));
        Assert.False(current.AllowsOperation(Stamp, Binding with { ResourceId = "replacement" }));
        Assert.False(current.AllowsOperation(Stamp, Binding with { DesiredHash = "unaccepted" }));
    }

    [Fact]
    public void Resource_mutation_requires_both_ownership_and_an_accepted_binding()
    {
        Assert.True(Execution().AllowsResourceMutation(Stamp, Ownership()));
        Assert.False(Execution().AllowsResourceMutation(Stamp, Ownership() with { TenantId = "other" }));
        Assert.False(Execution().AllowsResourceMutation(Stamp, Ownership() with { SourceId = "other" }));
        Assert.False(Execution().AllowsResourceMutation(Stamp, Ownership() with { ResourceId = "other" }));
        Assert.False(Execution().AllowsResourceMutation(Stamp, Ownership() with { Name = "other" }));
    }

    [Theory]
    [InlineData(SnapshotApplyOutcome.Blocked)]
    [InlineData(SnapshotApplyOutcome.StaleExecution)]
    public void An_unsuccessful_configuration_preserves_the_last_applied_commit(SnapshotApplyOutcome outcome)
    {
        var result = Execution().Complete(outcome);
        Assert.Equal("working-commit", result.AppliedCommit);
        Assert.Equal("new-commit", result.AttemptedCommit);
    }

    [Fact]
    public void Configuration_application_advances_applied_commit_without_runtime_readiness()
    {
        var result = Execution().Complete(SnapshotApplyOutcome.Applied);
        Assert.IsType<AppliedSnapshot>(result.Progress);
        Assert.Equal("new-commit", result.AppliedCommit);
    }

    [Fact]
    public void Unsupported_schema_preserves_the_working_commit_and_has_its_own_progress()
    {
        var rejected = Execution().RejectStoredSchema();
        Assert.IsType<UnsupportedSnapshotSchema>(rejected.Progress);
        Assert.Equal("working-commit", rejected.AppliedCommit);
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
    public void Invalid_or_duplicate_accepted_bindings_cannot_create_an_execution()
    {
        Assert.Throws<ArgumentException>(() => SnapshotExecutionFactory.Create(Stamp, "origin", "commit", null,
            [Binding, Binding with { ResourceId = "second" }], new ApplyingSnapshot()));
        Assert.Throws<ArgumentException>(() => SnapshotExecutionFactory.Create(Stamp, "origin", "commit", null,
            [Binding with { DesiredHash = "" }], new ApplyingSnapshot()));
        Assert.Throws<ArgumentException>(() => SnapshotExecutionFactory.Create(Stamp with { Attempt = -1 }, "origin", "commit", null,
            [Binding], new ApplyingSnapshot()));
    }

    [Fact]
    public void Retry_policy_keeps_inflight_lease_blocked_retry_and_terminal_schema_distinct()
    {
        var now = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now + SnapshotExecutionPolicy.RecoveryLease, SnapshotExecutionPolicy.NextAttemptAt(new ApplyingSnapshot(), now));
        Assert.Equal(now + SnapshotExecutionPolicy.RetryDelay, SnapshotExecutionPolicy.NextAttemptAt(new BlockedSnapshot(), now));
        Assert.Null(SnapshotExecutionPolicy.NextAttemptAt(new AppliedSnapshot(), now));
        Assert.Null(SnapshotExecutionPolicy.NextAttemptAt(new UnsupportedSnapshotSchema(), now));
    }
}
