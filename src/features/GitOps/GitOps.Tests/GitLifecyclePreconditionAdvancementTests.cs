using System.Collections.Immutable;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitLifecyclePreconditionAdvancementTests
{
    private static readonly GitResourceIdentity First = new("Binding", "first", "first");
    private static readonly GitResourceIdentity Second = new("Binding", "second", "second");

    [Fact]
    public void Own_receipt_advances_shared_document_versions_without_rebinding_other_resources()
    {
        var operations = new[] { Operation(First), Operation(Second) }.ToImmutableArray();
        var after = Expected(First, 5) with { OwnerSourceId = "source" };
        var result = GitLifecyclePreconditionAdvancement.Advance(operations,
            [new(1, First.ResourceId, GitLifecycleStep.Adopt, after, 1, [], First)]);

        Assert.Equal(after, result[0].Expected);
        Assert.Equal(Second, result[1].Resource);
        Assert.Null(result[1].Expected!.OwnerSourceId);
        Assert.Equal(5, result[1].Expected!.Versions.Single().Revision);
    }

    [Fact]
    public void Receipt_cannot_authorize_an_unreviewed_action_or_different_identity()
    {
        var operations = new[] { Operation(First) }.ToImmutableArray();
        Assert.Throws<InvalidOperationException>(() => GitLifecyclePreconditionAdvancement.Advance(operations,
            [new(1, "unreviewed", GitLifecycleStep.Adopt, Expected(First, 5), 1, [], First)]));
        Assert.Throws<InvalidOperationException>(() => GitLifecyclePreconditionAdvancement.Advance(operations,
            [new(1, First.ResourceId, GitLifecycleStep.Adopt, Expected(Second, 5), 1, [], First)]));
    }

    [Fact]
    public void Recovery_applies_causal_receipt_order_and_keeps_unrelated_tokens()
    {
        var first = Operation(First);
        var second = Operation(Second) with
        {
            Expected = Expected(Second, 4) with { Versions = [new("unrelated", 9)] }
        };
        var result = GitLifecyclePreconditionAdvancement.Advance([first, second],
            [new(2, First.ResourceId, GitLifecycleStep.BeginRemoval, Expected(First, 6), 0, [], First),
                new(1, First.ResourceId, GitLifecycleStep.Adopt, Expected(First, 5), 0, [], First)]);
        Assert.Equal(6, result[0].Expected!.Versions.Single().Revision);
        Assert.Equal(new GitResourceVersion("unrelated", 9), result[1].Expected!.Versions.Single());
    }

    [Fact]
    public void Completed_child_deletion_advances_its_surviving_parent_fence()
    {
        var result = GitLifecyclePreconditionAdvancement.Advance([Operation(First), Operation(Second)],
            [new(1, First.ResourceId, GitLifecycleStep.RemovalCompleted, null, 1, [new("shared-parent", 5)], First)]);
        Assert.Null(result[0].Expected);
        Assert.Equal(5, result[1].Expected!.Versions.Single().Revision);
    }

    [Fact]
    public void Replacement_application_advances_only_the_reviewed_new_subject_and_keeps_the_old_identity()
    {
        var replacement = new GitResourceIdentity(First.Kind, First.Name, "new-id");
        var operation = Operation(First) with { Action = GitLifecycleAction.Replace, Replacement = replacement };
        var result = GitLifecyclePreconditionAdvancement.Advance([operation],
            [new(1, First.ResourceId, GitLifecycleStep.RemovalCompleted, null, 1, [], First),
                new(2, First.ResourceId, GitLifecycleStep.Apply, Expected(replacement, 1), 1, [], replacement)]);
        Assert.Equal(First, result[0].Resource);
        Assert.Equal(replacement, result[0].Replacement);
        Assert.Null(result[0].Expected);
        Assert.Throws<InvalidOperationException>(() => GitLifecyclePreconditionAdvancement.Advance([operation],
            [new(1, First.ResourceId, GitLifecycleStep.Apply, Expected(Second, 1), 1, [], Second)]));
    }

    private static GitPreviewOperation Operation(GitResourceIdentity resource) =>
        new(resource.ResourceId, resource, GitLifecycleAction.Adopt, Expected(resource, 4), null, [], [], [],
            GitLifecycleProtection.ConfigurationOnly);

    private static GitResourcePreconditions Expected(GitResourceIdentity resource, long revision) =>
        new(resource, null, [new("shared-parent", revision)], "configuration");
}
