using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitLifecycleReceiptCompletionTests
{
    [Fact]
    public void Inventory_only_detach_is_complete_after_its_exact_terminal_receipt()
    {
        var (plan, receipt) = Detach();
        Assert.IsType<GitPreviewInventoryInput>(plan.Source.Input);
        Assert.True(GitLifecycleReceiptCompletion.HasCompleteActions(plan, 7, [receipt]));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tenant")]
    [InlineData("source")]
    [InlineData("run")]
    [InlineData("fence")]
    [InlineData("kind")]
    [InlineData("name")]
    [InlineData("id")]
    [InlineData("subject")]
    [InlineData("action")]
    [InlineData("step")]
    [InlineData("blocked")]
    public void A_missing_or_unrelated_receipt_cannot_complete_inventory_only_detach(string scenario)
    {
        var (plan, receipt) = Detach();
        var changed = scenario switch
        {
            "missing" => receipt,
            "tenant" => receipt with { TenantId = "other" },
            "source" => receipt with { SourceId = "other" },
            "run" => receipt with { RunId = "other" },
            "fence" => receipt with { Fence = 8 },
            "kind" => receipt with { Kind = "Other" },
            "name" => receipt with { Name = "other" },
            "id" => receipt with { ResourceId = "other" },
            "subject" => receipt with
            {
                Lifecycle = receipt.Lifecycle! with
                {
                    Subject = new GitResourceIdentity("Widget", "kept", "other"),
                },
            },
            "action" => receipt with { Lifecycle = receipt.Lifecycle! with { ActionId = "other" } },
            "step" => receipt with { Lifecycle = receipt.Lifecycle! with { Step = GitLifecycleStep.BeginRemoval } },
            "blocked" => receipt with { Blocker = "blocked" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.False(GitLifecycleReceiptCompletion.HasCompleteActions(plan, 7, scenario == "missing" ? [] : [changed]));
    }

    private static (GitLifecycleExecutionPlan Plan, GitOperationReceipt Receipt) Detach()
    {
        var identity = new GitResourceIdentity("Widget", "kept", "resource");
        var source = new GitPreviewSourceStamp(
            new GitSourceIdentity("tenant", "source"),
            1,
            1,
            new GitPreviewInventoryInput());
        var expected = new GitResourcePreconditions(
            identity,
            "source",
            [new GitResourceVersion("Widget/resource", 1)],
            "configuration");
        var operation = new GitPreviewOperation(
            "detach-resource",
            identity,
            GitLifecycleAction.Detach,
            expected,
            null,
            [],
            [],
            [],
            GitLifecycleProtection.ConfigurationOnly);
        var now = DateTime.UtcNow;
        var preview = GitOperationPreviewFactory.Create(
            "preview",
            source,
            new GitPreviewDetachSource(),
            [operation],
            now);
        var confirmed = preview.Confirm(
            new GitPreviewConfirmationContext(
                preview.Id,
                preview.Revision,
                source,
                [new GitPreviewObservation(identity, expected)],
                "user",
                now,
                "run"));
        var plan = GitLifecycleExecutionPlanFactory.Create(confirmed);
        var receipt = new GitOperationReceipt(
            "receipt",
            "tenant",
            "source",
            "run",
            7,
            identity.Kind,
            identity.Name,
            "detach",
            identity.ResourceId,
            "hash",
            null,
            false,
            1,
            new GitLifecycleEffect(1, operation.Id, GitLifecycleStep.Detach, null, 2, [], identity));
        return (plan, receipt);
    }
}
