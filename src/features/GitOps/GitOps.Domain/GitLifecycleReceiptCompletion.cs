using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Domain;

public static class GitLifecycleReceiptCompletion
{
    public static bool HasCompleteActions(
        GitLifecycleExecutionPlan plan,
        long fence,
        IEnumerable<GitOperationReceipt> receipts)
    {
        var runId = plan.Authorization switch
        {
            GitPreviewConfirmed confirmed => confirmed.RunId,
            GitPreviewPolicyAuthorized policy => policy.RunId,
            GitPreviewAwaitingConfirmation => throw new InvalidOperationException(
                "Completion requires an authorized lifecycle plan."),
        };
        var scoped = receipts.Where(item => item.TenantId == plan.Source.Source.TenantId &&
                item.SourceId == plan.Source.Source.SourceId && item.RunId == runId && item.Fence == fence)
            .ToArray();
        return plan.Operations.All(operation => IsComplete(
            operation,
            scoped.Where(item =>
                item.Kind == operation.Resource.Kind && item.Name == operation.Resource.Name &&
                item.ResourceId == operation.Resource.ResourceId && item.Lifecycle?.Subject == operation.Resource)));
    }

    public static bool IsComplete(GitPreviewOperation operation, IEnumerable<GitOperationReceipt> receipts)
    {
        if (operation.Action == GitLifecycleAction.Apply)
            return true;
        var step = operation.Action switch
        {
            GitLifecycleAction.Adopt => GitLifecycleStep.Adopt,
            GitLifecycleAction.Remove or GitLifecycleAction.Replace => GitLifecycleStep.RemovalCompleted,
            GitLifecycleAction.Detach => GitLifecycleStep.Detach,
            GitLifecycleAction.Retain => GitLifecycleStep.Retain,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        return receipts.Any(item => item.Blocker is null && item.Lifecycle is { } effect &&
            effect.ActionId == operation.Id && effect.Step == step);
    }
}
