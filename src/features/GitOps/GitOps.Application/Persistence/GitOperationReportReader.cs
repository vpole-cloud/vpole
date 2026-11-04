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
    IMongoCollection<GitOperationReceipt> receipts)
{
    internal async Task<IReadOnlyDictionary<string, GitRunOperations>> ReadAsync(string tenant,
        IReadOnlyCollection<GitReconciliationRun> runs, CancellationToken ct)
    {
        var accepted = runs.Where(item => item.Input is GitInputAccepted).ToArray();
        if (accepted.Length == 0)
            return new Dictionary<string, GitRunOperations>();

        var ids = accepted.Select(item => item.Id).ToArray();
        var metadata = await plans.Find(GitOperationPlanFilters.Runs(tenant, ids))
            .Project(item => new GitOperationPlanMetadata(item.Stamp.Run.RunId, item.Stamp.Resource.Kind,
                item.Stamp.Resource.Name, item.Stamp.Phase, item.Revision, item.Decision.Action,
                item.EvaluatedAtUtc, item.Decision.ChangedFields)).ToListAsync(ct);
        var results = await receipts.Find(GitOperationReceiptFilters.Runs(tenant, ids)).ToListAsync(ct);
        var byPlan = metadata.ToDictionary(item => (item.RunId, item.Kind, item.Name, item.Phase.ToString()));
        var byReceipt = results.ToDictionary(item => (item.RunId, item.Kind, item.Name, item.Phase));
        return accepted.ToDictionary(run => run.Id, run =>
        {
            var items = ((GitInputAccepted)run.Input).Snapshot.Operations.Select(operation =>
            {
                var key = (run.Id, operation.Kind, operation.Name, operation.Phase.ToString());
                var plan = byPlan.GetValueOrDefault(key);
                var result = byReceipt.GetValueOrDefault(key);
                if (result?.PlanRevision != plan?.Revision)
                    result = null;

                return new RepositoryOperationView(operation.Kind, operation.Name, operation.ResourceId,
                    operation.Phase, result is null ? RepositoryOperationStatus.Pending :
                    result.Blocker is null ? RepositoryOperationStatus.Succeeded : RepositoryOperationStatus.Blocked,
                    result?.Blocker, result?.Changed ?? false, plan is null ? null :
                    new(plan.Action, plan.Revision, plan.EvaluatedAtUtc, plan.ChangedFields));
            }).ToArray();
            return new GitRunOperations(items, Summary(items));
        });
    }

    private static RepositoryOperationSummary Summary(IReadOnlyCollection<RepositoryOperationView> items) => new(
        items.Count(item => item.Status == RepositoryOperationStatus.Succeeded &&
                            item.Plan?.Action == GitPlannedAction.Create),
        items.Count(item => item.Status == RepositoryOperationStatus.Succeeded &&
                            item.Plan?.Action == GitPlannedAction.Update),
        items.Count(item => item.Status == RepositoryOperationStatus.Succeeded &&
                            item.Plan?.Action == GitPlannedAction.NoOp),
        items.Count(item => item.Status == RepositoryOperationStatus.Blocked),
        items.Count(item => item.Status == RepositoryOperationStatus.Pending));
}
