using MongoDB.Driver;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Application.Endpoints.Sources;

namespace Pole.Features.GitOps.Application.Persistence;

public sealed class GitRepositoryQueries(
    IMongoCollection<RepositorySource> sources,
    IMongoCollection<GitSynchronizationState> states,
    IMongoCollection<GitReconciliationRun> runs,
    IMongoCollection<ManagedDeclaration> inventory,
    GitOperationReportReader reports)
{
    public async Task<RepositorySourceView?> SourceAsync(string tenant, string id, CancellationToken ct)
    {
        var source = await sources.Find(GitRepositoryFilters.VisibleSource(tenant, id)).FirstOrDefaultAsync(ct);
        return source is null ? null : (await ViewsAsync(tenant, [source], ct)).Single();
    }

    public async Task<RepositorySourcePage> ListAsync(string tenant, int page, int pageSize, CancellationToken ct)
    {
        var items = await sources.Find(GitRepositoryFilters.VisibleTenant(tenant)).SortBy(item => item.Id)
            .Skip((page - 1) * pageSize).Limit(pageSize + 1).ToListAsync(ct);
        return new(await ViewsAsync(tenant, items.Take(pageSize).ToArray(), ct),
            page,
            pageSize,
            items.Count > pageSize);
    }

    private async Task<RepositorySourceView[]> ViewsAsync(
        string tenant,
        IReadOnlyCollection<RepositorySource> items,
        CancellationToken ct)
    {
        if (items.Count == 0)
            return [];

        var ids = items.Select(item => item.Id).ToArray();
        var synchronization = await states.Find(GitRepositoryFilters.States(tenant, ids)).ToListAsync(ct);
        var latestIds = synchronization.Select(item => item.LatestRunId).OfType<string>().ToArray();
        var latest = await runs.Find(GitRepositoryFilters.Runs(tenant, latestIds))
            .ToListAsync(ct);
        var counts = await inventory.Aggregate().Match(GitRepositoryFilters.Inventory(tenant, ids))
            .Group(item => item.SourceId,
                group => new
                {
                    SourceId = group.Key,
                    Owned = group.LongCount(),
                    Pending = group.Sum(item => item.PendingRemoval ? 1L : 0L)
                }).ToListAsync(ct);
        var operations = await reports.ReadAsync(tenant, latest, ct);
        var bySource = synchronization.ToDictionary(item => item.Source.SourceId);
        var byRun = latest.ToDictionary(item => item.Id);
        var byCount = counts.ToDictionary(item => item.SourceId);
        return items.Select(source =>
        {
            var state = bySource[source.Id];
            var run = state.LatestRunId is { } runId ? byRun.GetValueOrDefault(runId) : null;
            var count = byCount.GetValueOrDefault(source.Id);
            return new RepositorySourceView(source.Id,
                source.Revision,
                new(source.Repository.Provider,
                    source.Repository.ConnectionId,
                    source.Repository.ExternalRepositoryId,
                    source.Repository.Branch,
                    source.Repository.ManifestDirectory,
                    source.DisplayUrl),
                (int)source.Sync.PollInterval.TotalSeconds,
                state.CurrentRun is null && state.RequestedGeneration > state.CompletedGeneration
                    ? RepositoryRunStatus.Queued
                    : run is null
                        ? RepositoryRunStatus.Queued
                        : Status(run),
                new(state.RequestedGeneration,
                    state.CompletedGeneration,
                    state.Commits.Observed,
                    state.Commits.Attempted,
                    state.Commits.FullyApplied,
                    state.CurrentRun?.RunId,
                    state.LatestRunId),
                new(count?.Owned ?? 0, count?.Pending ?? 0, state.InventoryRevision),
                run is null ? null : ToView(run, operations.GetValueOrDefault(run.Id)?.Summary),
                source.Activity, source.Prune);
        }).ToArray();
    }

    public async Task<RepositoryRunPage?> RunsAsync(
        string tenant,
        string source,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        if (!await sources.Find(GitRepositoryFilters.VisibleSource(tenant, source)).AnyAsync(ct))
            return null;

        var items = await runs.Find(GitRepositoryFilters.SourceRuns(tenant, source))
            .SortByDescending(item => item.Stamp.Generation).ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize).Limit(pageSize + 1).ToListAsync(ct);
        var selected = items.Take(pageSize).ToArray();
        var operations = await reports.ReadAsync(tenant, selected, ct);
        return new(selected.Select(run => ToView(run, operations.GetValueOrDefault(run.Id)?.Summary)).ToArray(),
            page, pageSize, items.Count > pageSize);
    }

    public async Task<RepositoryRunView?> RunAsync(string tenant, string source, string runId, CancellationToken ct)
    {
        if (!await sources.Find(GitRepositoryFilters.VisibleSource(tenant, source)).AnyAsync(ct))
            return null;

        var run = await runs.Find(GitRepositoryFilters.Run(tenant, source, runId)).FirstOrDefaultAsync(ct);
        if (run is null)
            return null;
        var operations = await reports.ReadAsync(tenant, [run], ct);
        return ToView(run, operations.GetValueOrDefault(run.Id)?.Summary);
    }

    public async Task<RepositoryOperationPage?> OperationsAsync(
        string tenant,
        string source,
        string runId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        if (!await sources.Find(GitRepositoryFilters.VisibleSource(tenant, source)).AnyAsync(ct))
            return null;

        var run = await runs.Find(GitRepositoryFilters.Run(tenant, source, runId)).FirstOrDefaultAsync(ct);
        if (run is null)
            return null;
        if (run.AcceptedSnapshot is null && run.Input is not GitInputLifecycle)
            return new([], page, pageSize, false);

        var report = (await reports.ReadAsync(tenant, [run], ct))[run.Id];
        var operations = report.Items.Skip((page - 1) * pageSize).Take(pageSize + 1).ToArray();
        return new(operations.Take(pageSize).ToArray(), page, pageSize, operations.Length > pageSize);
    }

    private static RepositoryRunView ToView(GitReconciliationRun run, RepositoryOperationSummary? operations) => new(
        run.Id,
        run.Stamp.Source.SourceId,
        run.Stamp.ConfigurationRevision,
        run.Stamp.Generation,
        run.Commit,
        run.RetryOfRunId,
        run.Initiator.Trigger.ToString(),
        run.Initiator.RequestedByUserId,
        Status(run),
        run.CreatedAtUtc,
        run.Diagnostics.Select(item => new RepositoryRunDiagnosticView(item.Code, item.File, item.Document, item.Field))
            .ToArray(), operations, run.Input switch
            {
                GitInputAwaitingApproval awaiting => new(awaiting.PreviewId,
                    RepositoryLifecycleAuthorization.AwaitingConfirmation),
                GitInputLifecycle lifecycle => new(lifecycle.Plan.PreviewId,
                    lifecycle.Plan.Authorization is GitPreviewPolicyAuthorized
                        ? RepositoryLifecycleAuthorization.PrunePolicy
                        : RepositoryLifecycleAuthorization.UserConfirmed),
                GitInputUnresolved or GitInputPinned or GitInputAccepted => null
            });

    private static RepositoryRunStatus Status(GitReconciliationRun run) => run.Input is GitInputAwaitingApproval
        ? RepositoryRunStatus.AwaitingConfirmation : run.State switch
        {
            GitRunWaiting => RepositoryRunStatus.Queued,
            GitRunDelayed => RepositoryRunStatus.WaitingRetry,
            GitRunWaitingCleanup => RepositoryRunStatus.PendingCleanup,
            GitRunClaimed => run.Input switch
            {
                GitInputUnresolved => RepositoryRunStatus.Resolving,
                GitInputPinned => RepositoryRunStatus.Fetching,
                GitInputAccepted => RepositoryRunStatus.Applying,
                GitInputLifecycle => RepositoryRunStatus.Applying,
                GitInputAwaitingApproval => RepositoryRunStatus.AwaitingConfirmation
            },
            GitRunFinished finished => finished.Completion switch
            {
                GitRunCompletion.ConfigurationApplied => RepositoryRunStatus.ConfigurationApplied,
                GitRunCompletion.AlreadyCurrent => RepositoryRunStatus.AlreadyCurrent,
                GitRunCompletion.Blocked => RepositoryRunStatus.Blocked,
                GitRunCompletion.Failed => RepositoryRunStatus.Failed,
                GitRunCompletion.Superseded => RepositoryRunStatus.Superseded,
                GitRunCompletion.LifecycleCompleted => RepositoryRunStatus.LifecycleCompleted,
                _ => throw new ArgumentOutOfRangeException(nameof(run))
            }
        };
}
