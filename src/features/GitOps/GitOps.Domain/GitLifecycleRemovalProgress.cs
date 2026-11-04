using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Domain;

public static class GitLifecycleRemovalProgress
{
    public static bool HasStarted(GitResourceIdentity resource, IEnumerable<GitOperationReceipt> receipts) =>
        receipts.Any(item => item.Blocker is null && item.Lifecycle is { } effect && effect.Subject == resource &&
            effect.Step is GitLifecycleStep.BeginRemoval or GitLifecycleStep.RemovalCompleted);

    public static bool CanBegin(GitResourceIdentity resource, IEnumerable<GitPreviewDependencySet> dependencies,
        IReadOnlyCollection<GitOperationReceipt> receipts) => dependencies.All(set =>
        set.Dependencies.All(dependency => dependency.Order switch
        {
            GitLifecycleDependencyOrder.BeforeRemoval => true,
            GitLifecycleDependencyOrder.AfterRemovalRequested => dependency.Dependent != resource ||
                HasStarted(set.Target, receipts),
            GitLifecycleDependencyOrder.AfterDependentRemovalRequested => set.Target != resource ||
                HasStarted(dependency.Dependent, receipts),
            _ => throw new ArgumentOutOfRangeException(nameof(dependencies))
        }));
}
