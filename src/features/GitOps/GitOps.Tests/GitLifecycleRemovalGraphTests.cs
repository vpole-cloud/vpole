// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitLifecycleRemovalGraphTests
{
    private static readonly GitResourceIdentity Workload = new("Widget", "workload", "workload");
    private static readonly GitResourceIdentity Configuration = new("Recipe", "configuration", "configuration");

    [Fact]
    public void A_dependent_request_can_unlock_configuration_cleanup_before_workload_finalization()
    {
        var dependencies = Sets(GitLifecycleDependencyOrder.AfterDependentRemovalRequested);
        Assert.True(GitLifecycleRemovalGraph.IsAcyclic([Operation(Workload), Operation(Configuration)], dependencies));
        Assert.True(GitLifecycleRemovalProgress.CanBegin(Workload, dependencies, []));
        Assert.False(GitLifecycleRemovalProgress.CanBegin(Configuration, dependencies, []));
        Assert.True(
            GitLifecycleRemovalProgress.CanBegin(
                Configuration,
                dependencies,
                [Receipt(Workload, GitLifecycleStep.BeginRemoval)]));
    }

    [Fact]
    public void A_synchronous_completed_deletion_also_proves_that_its_request_was_accepted()
    {
        Assert.True(
            GitLifecycleRemovalProgress.CanBegin(
                Configuration,
                Sets(GitLifecycleDependencyOrder.AfterDependentRemovalRequested),
                [Receipt(Workload, GitLifecycleStep.RemovalCompleted)]));
    }

    [Fact]
    public void A_request_waiting_for_dependent_finalization_rejects_the_expanded_step_cycle()
    {
        Assert.False(
            GitLifecycleRemovalGraph.IsAcyclic(
                [Operation(Workload), Operation(Configuration)],
                Sets(GitLifecycleDependencyOrder.BeforeRemoval)));
    }

    private static GitPreviewDependencySet[] Sets(GitLifecycleDependencyOrder order) =>
    [
        new("ImageReference", Configuration, [new GitPreviewDependency(Workload, [], order)]),
        new(
            "Recipe",
            Workload,
            [new GitPreviewDependency(Configuration, [], GitLifecycleDependencyOrder.AfterRemovalRequested)]),
    ];

    private static GitPreviewOperation Operation(GitResourceIdentity resource) => new(
        resource.ResourceId,
        resource,
        GitLifecycleAction.Remove,
        new GitResourcePreconditions(resource, "source", [new GitResourceVersion(resource.ResourceId, 1)], "hash"),
        null,
        [],
        [],
        [],
        GitLifecycleProtection.ConfigurationOnly);

    private static GitOperationReceipt Receipt(GitResourceIdentity resource, GitLifecycleStep step) => new(
        resource.ResourceId,
        "tenant",
        "source",
        "run",
        1,
        resource.Kind,
        resource.Name,
        "Lifecycle",
        resource.ResourceId,
        "hash",
        null,
        true,
        1,
        new GitLifecycleEffect(1, resource.ResourceId, step, null, 1, [], resource));
}
