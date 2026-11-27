// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitPruneAuthorizationTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly GitRepositoryLocation Repository = new("Example", "connection", "repository", "main", "");
    private static readonly GitResourceIdentity Removed = new("Widget", "old", "old-id");
    private static readonly GitResourceIdentity Kept = new("Widget", "kept", "kept-id");

    private static readonly GitAcceptedSnapshot Snapshot = new(
        new string('a', 40),
        "desired",
        "v1",
        [new AcceptedDeclaration("Widget", "kept", "hash", "{}", "kept-id")],
        [new GitAcceptedOperation("Widget", "kept", "kept-id", DeclarationPhase.Prepare)]);

    [Fact]
    public void Policy_authority_is_typed_bound_to_the_same_run_and_round_trips_without_a_user()
    {
        var preview = Preview();
        Assert.True(preview.CanAuthorizePrune());
        var authorized = preview.AuthorizePrune(new GitPreviewPolicyAuthorizationContext(1, Now, "run"));
        var policy = Assert.IsType<GitPreviewPolicyAuthorized>(authorized.State);
        Assert.Equal("run", policy.RunId);
        var run = Run().AuthorizePrune(authorized);
        Assert.IsType<GitInputLifecycle>(run.Input);
        RoundTrip(run);
        Assert.Throws<InvalidOperationException>(() => (Run() with { Id = "other" }).AuthorizePrune(authorized));
        Assert.Throws<InvalidOperationException>(() =>
            preview.AuthorizePrune(new GitPreviewPolicyAuthorizationContext(2, Now, "run")));
    }

    [Theory]
    [InlineData("protected")]
    [InlineData("missing-created")]
    [InlineData("empty")]
    [InlineData("ambiguous-rename")]
    [InlineData("adoption")]
    [InlineData("replacement")]
    [InlineData("retained-removals")]
    public void Dangerous_plans_cannot_gain_policy_authority(string danger)
    {
        var preview = Preview();
        preview = danger switch
        {
            "retained-removals" => preview with
            {
                Intent = new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Retain),
            },
            "protected" => preview with
            {
                Operations = preview.Operations.SetItem(
                    0,
                    preview.Operations[0] with { Protection = GitLifecycleProtection.ProtectedData }),
            },
            "missing-created" => preview with
            {
                Operations = preview.Operations.SetItem(0, preview.Operations[0] with { Expected = null }),
            },
            "empty" => preview with
            {
                Source = preview.Source with
                {
                    Input = new GitPreviewRepositoryInput(
                        Repository,
                        Snapshot with { Resources = [], Operations = [] }),
                },
            },
            "ambiguous-rename" => preview with
            {
                Operations = preview.Operations.SetItem(1, preview.Operations[1] with { Expected = null }),
            },
            "adoption" => preview with
            {
                Operations = preview.Operations.SetItem(
                    1,
                    preview.Operations[1] with { Action = GitLifecycleAction.Adopt }),
            },
            "replacement" => preview with
            {
                Operations = preview.Operations.SetItem(
                    0,
                    preview.Operations[0] with { Action = GitLifecycleAction.Replace, Replacement = Kept }),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(danger)),
        };
        Assert.False(preview.CanAuthorizePrune());
        Assert.Throws<InvalidOperationException>(() =>
            preview.AuthorizePrune(new GitPreviewPolicyAuthorizationContext(1, Now, "run")));
    }

    [Fact]
    public void Waiting_for_confirmation_preserves_input_and_has_no_execution_authority()
    {
        var run = Run().AwaitApproval(Preview());
        Assert.Equal(Snapshot.Commit, run.Commit);
        Assert.Equal(Snapshot, run.AcceptedSnapshot);
        Assert.Equal("preview", Assert.IsType<GitInputAwaitingApproval>(run.Input).PreviewId);
        Assert.Throws<InvalidOperationException>(() => GitLifecycleExecutionPlanFactory.Create(Preview()));
        RoundTrip(run);
    }

    [Fact]
    public void Retain_completion_requires_its_own_receipt_and_never_counts_a_delete_request()
    {
        var operation = Preview().Operations[0] with { Action = GitLifecycleAction.Retain };

        GitOperationReceipt Receipt(GitLifecycleStep step) => new(
            "receipt",
            "tenant",
            "source",
            "run",
            1,
            "Widget",
            "old",
            "Lifecycle",
            "old-id",
            "hash",
            null,
            false,
            1,
            new GitLifecycleEffect(1, operation.Id, step, operation.Expected, 1, [], operation.Resource));

        Assert.False(GitLifecycleReceiptCompletion.IsComplete(operation, [Receipt(GitLifecycleStep.BeginRemoval)]));
        Assert.True(GitLifecycleReceiptCompletion.IsComplete(operation, [Receipt(GitLifecycleStep.Retain)]));
    }

    private static GitReconciliationRun Run() => GitReconciliationRunFactory.Create(
            new GitRunStamp(new GitSourceIdentity("tenant", "source"), "run", 1, 1, 1),
            Repository,
            new GitRunInitiator(GitRunTrigger.Poll, null),
            Now,
            new GitInputPinned(Snapshot.Commit),
            null)
        .Accept(Snapshot);

    private static GitOperationPreview Preview() => GitOperationPreviewFactory.Create(
        "preview",
        new GitPreviewSourceStamp(
            new GitSourceIdentity("tenant", "source"),
            1,
            1,
            new GitPreviewRepositoryInput(Repository, Snapshot)),
        new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove),
        [
            new GitPreviewOperation(
                "remove",
                Removed,
                GitLifecycleAction.Remove,
                new GitResourcePreconditions(Removed, "source", [new GitResourceVersion("Widget/old-id", 1)], "old"),
                null,
                [],
                [],
                [],
                GitLifecycleProtection.ConfigurationOnly),
            new GitPreviewOperation(
                "apply",
                Kept,
                GitLifecycleAction.Apply,
                new GitResourcePreconditions(Kept, "source", [new GitResourceVersion("Widget/kept-id", 1)], "kept"),
                null,
                [],
                [],
                [],
                GitLifecycleProtection.ConfigurationOnly),
        ],
        Now);

    private static void RoundTrip(GitReconciliationRun run)
    {
        var bson = run.ToBsonDocument();
        Assert.Equal(bson, BsonSerializer.Deserialize<GitReconciliationRun>(bson).ToBsonDocument());
    }
}
