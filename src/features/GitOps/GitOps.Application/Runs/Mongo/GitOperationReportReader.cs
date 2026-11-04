using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Persistence;

internal sealed record GitOperationPlanMetadata(
    string RunId,
    string Kind,
    string Name,
    DeclarationPhase Phase,
    long Revision,
    GitPlannedAction Action,
    DateTime EvaluatedAtUtc,
    ImmutableArray<string> ChangedFields);

internal sealed record GitRunOperations(
    RepositoryOperationView[] Items,
    RepositoryOperationSummary Summary);

public sealed class GitOperationReportReader(
    IMongoCollection<GitPlannedOperation> plans,
    IMongoCollection<GitOperationReceipt> receipts,
    IMongoCollection<GitOperationPreview> previews)
{
    internal async Task<IReadOnlyDictionary<string, GitRunOperations>> ReadAsync(string tenant,
        IReadOnlyCollection<GitReconciliationRun> runs, CancellationToken ct)
    {
        var accepted = runs.Where(item => item.AcceptedSnapshot is not null || item.Input is GitInputLifecycle)
            .ToArray();
        if (accepted.Length == 0)
            return new Dictionary<string, GitRunOperations>();

        var ids = accepted.Select(item => item.Id).ToArray();
        var metadata = await plans.Find(GitOperationPlanFilters.Runs(tenant, ids))
            .Project(item => new GitOperationPlanMetadata(item.Stamp.Run.RunId, item.Stamp.Resource.Kind,
                item.Stamp.Resource.Name, item.Stamp.Phase, item.Revision, item.Decision.Action,
                item.EvaluatedAtUtc, item.Decision.ChangedFields)).ToListAsync(ct);
        var results = await receipts.Find(GitOperationReceiptFilters.Runs(tenant, ids)).ToListAsync(ct);
        var previewIds = accepted.Select(item => item.Input).OfType<GitInputAwaitingApproval>()
            .Select(item => item.PreviewId).ToArray();
        var awaiting = await previews.Find(GitLifecyclePreviewFilters.TenantPreviews(tenant, previewIds))
            .ToListAsync(ct);
        var byPreview = awaiting.ToDictionary(item => item.Id);
        var byPlan = metadata.ToDictionary(item => (item.RunId, item.Kind, item.Name, item.Phase.ToString()));
        var byReceipt = results.Where(item => item.Lifecycle is null or { Step: GitLifecycleStep.Apply })
            .ToDictionary(item => (item.RunId, item.Kind, item.Name, item.Phase));
        return accepted.ToDictionary(run => run.Id, run =>
        {
            var awaitingApproval = run.Input is GitInputAwaitingApproval;
            var items = (run.AcceptedSnapshot?.Operations ?? []).Select(operation =>
            {
                var key = (run.Id, operation.Kind, operation.Name, operation.Phase.ToString());
                var plan = byPlan.GetValueOrDefault(key);
                var result = byReceipt.GetValueOrDefault(key);
                if (result?.PlanRevision != plan?.Revision)
                    result = null;

                return new RepositoryOperationView(operation.Kind, operation.Name, operation.ResourceId,
                    operation.Phase, result is null ? awaitingApproval
                        ? RepositoryOperationStatus.AwaitingConfirmation :
                    RepositoryOperationStatus.Pending : result.Blocker is null ? RepositoryOperationStatus.Succeeded :
                    RepositoryOperationStatus.Blocked, result?.Blocker, result?.Changed ?? false, plan is null ? null :
                    new(plan.Action, plan.Revision, plan.EvaluatedAtUtc, plan.ChangedFields), null);
            }).ToList();
            var actions = run.Input switch
            {
                GitInputLifecycle lifecycle => lifecycle.Plan.Operations,
                GitInputAwaitingApproval approval => byPreview.GetValueOrDefault(approval.PreviewId)?.Operations ?? [],
                GitInputUnresolved or GitInputPinned or GitInputAccepted => []
            };
            var stored = results.Where(item => item.RunId == run.Id && item.Fence == run.Stamp.Fence).ToArray();
            foreach (var action in actions.Where(item => item.Action != GitLifecycleAction.Apply))
            {
                var effects = stored.Where(item => item.Lifecycle?.ActionId == action.Id && item.Blocker is null)
                    .OrderBy(item => item.Lifecycle!.Sequence).ToArray();
                var complete = GitLifecycleReceiptCompletion.IsComplete(action, effects);
                var pendingCleanup = !complete && effects.Any(item =>
                    item.Lifecycle!.Step == GitLifecycleStep.BeginRemoval);
                var terminalBlocker = !complete && !awaitingApproval && run.State is GitRunFinished
                { Completion: GitRunCompletion.Blocked or GitRunCompletion.Failed };
                var status = complete ? RepositoryOperationStatus.Succeeded : awaitingApproval
                    ? RepositoryOperationStatus.AwaitingConfirmation : pendingCleanup
                        ? RepositoryOperationStatus.PendingCleanup : terminalBlocker
                            ? RepositoryOperationStatus.Blocked : RepositoryOperationStatus.Pending;
                items.Add(new(action.Resource.Kind, action.Resource.Name, action.Resource.ResourceId, null, status,
                    terminalBlocker ? run.Diagnostics.FirstOrDefault()?.Code : null,
                    effects.Any(item => item.Changed), null, new(action.Id, action.Action,
                        effects.LastOrDefault()?.Lifecycle!.Step.ToString(), action.Replacement?.ResourceId,
                        action.Replacement?.Name, action.ChangedFields)));
            }
            var array = items.ToArray();
            return new GitRunOperations(array, Summary(array));
        });
    }

    private static RepositoryOperationSummary Summary(IReadOnlyCollection<RepositoryOperationView> items)
    {
        var lifecycle = items.Where(item => item.Lifecycle is not null).ToArray();
        return new(
            items.Count(item => item.Status == RepositoryOperationStatus.Succeeded &&
                                item.Plan?.Action == GitPlannedAction.Create),
            items.Count(item => item.Status == RepositoryOperationStatus.Succeeded &&
                                item.Plan?.Action == GitPlannedAction.Update),
            items.Count(item => item.Status == RepositoryOperationStatus.Succeeded &&
                                item.Plan?.Action == GitPlannedAction.NoOp),
            items.Count(item => item.Status == RepositoryOperationStatus.Blocked),
            items.Count(item => item.Status is RepositoryOperationStatus.Pending or
                RepositoryOperationStatus.PendingCleanup or RepositoryOperationStatus.AwaitingConfirmation),
            lifecycle.Length == 0 ? null : new(
                lifecycle.Count(item => item.Lifecycle!.Action == GitLifecycleAction.Adopt),
                lifecycle.Count(item => item.Lifecycle!.Action == GitLifecycleAction.Remove),
                lifecycle.Count(item => item.Lifecycle!.Action == GitLifecycleAction.Replace),
                lifecycle.Count(item => item.Lifecycle!.Action == GitLifecycleAction.Retain),
                lifecycle.Count(item => item.Lifecycle!.Action == GitLifecycleAction.Detach),
                lifecycle.Count(item => item.Status == RepositoryOperationStatus.PendingCleanup),
                lifecycle.Count(item => item.Status == RepositoryOperationStatus.AwaitingConfirmation)));
    }
}
