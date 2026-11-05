using Pole.Features.GitOps.Application.Mongo;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Sources;

public sealed record ChangeGitSourceActivity(
    string TenantId,
    string SourceId,
    long ExpectedRevision,
    string ActorUserId);

public sealed record DeleteGitSource(
    string TenantId,
    string SourceId,
    long ExpectedRevision);

public sealed record GitSourceDeletionRecorded;

public sealed record GitSourceDetachRequired(long OwnedResources);

public union ChangeGitSourceActivityOutcome(
    RepositorySource,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceInactive);

public union DeleteGitSourceOutcome(
    GitSourceDeletionRecorded,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceDetachRequired,
    GitSourceBusy);

public sealed class GitSourceLifecycleStore(
    IMongoCollection<RepositorySource> sources,
    IMongoCollection<GitSynchronizationState> states,
    IMongoCollection<GitReconciliationRun> runs,
    IMongoCollection<ManagedDeclaration> inventory,
    GitRepositorySources repositorySources,
    MongoRunTransaction transactions)
{
    public Task<ChangeGitSourceActivityOutcome> PauseAsync(ChangeGitSourceActivity request, CancellationToken ct) =>
        ChangeActivityAsync(request, GitSourceActivity.Paused, ct);

    public Task<ChangeGitSourceActivityOutcome> ResumeAsync(ChangeGitSourceActivity request, CancellationToken ct) =>
        ChangeActivityAsync(request, GitSourceActivity.Active, ct);

    private Task<ChangeGitSourceActivityOutcome> ChangeActivityAsync(
        ChangeGitSourceActivity request,
        GitSourceActivity activity,
        CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<ChangeGitSourceActivityOutcome>(async (tx, token) =>
        {
            var source = await sources.Find(tx.Session,
                GitRepositoryFilters.VisibleSource(request.TenantId, request.SourceId)).FirstOrDefaultAsync(token);
            if (source is null)
                return tx.Abort(new GitSourceNotFound());

            if (source.Revision != request.ExpectedRevision)
                return tx.Abort(new GitSourceRevisionConflict());

            if (source.Activity is GitSourceActivity.Detached or GitSourceActivity.Deleted ||
                activity == GitSourceActivity.Active && source.Activity != GitSourceActivity.Paused)
                return tx.Abort(new GitSourceInactive(source.Activity));

            var state = await states.Find(tx.Session, GitRepositoryFilters.State(source.TenantId, source.Id))
                .SingleAsync(token);
            if (source.Activity == activity && state.CurrentRun is null &&
                state.RequestedGeneration == state.CompletedGeneration)
                return tx.Commit(source);

            var now = await new GitDatabaseClock(sources).NowAsync(new(source.TenantId, source.Id), tx.Session, token);
            await SupersedeAsync(source, state, now, tx.Session, token);
            var next = activity == GitSourceActivity.Active ? source.Resume() : source.Pause();
            if (next.Revision == source.Revision)
                next = next with { Revision = checked(source.Revision + 1) };

            var nextState = StopWork(state) with { NextPollAtUtc = now + source.Sync.PollInterval };
            if (activity == GitSourceActivity.Active)
                nextState = nextState.RequestSync(new(new(GitRunTrigger.Manual, request.ActorUserId), null));

            await sources.ReplaceOneAsync(tx.Session, GitRepositoryFilters.Source(source.TenantId, source.Id),
                next, cancellationToken: token);
            await states.ReplaceOneAsync(tx.Session, GitRepositoryFilters.State(source.TenantId, source.Id),
                nextState, cancellationToken: token);
            if (activity == GitSourceActivity.Active)
                await repositorySources.WakeAsync(next, tx.Session, token);

            return tx.Commit(next);
        }, ct);

    public Task<DeleteGitSourceOutcome> DeleteAsync(DeleteGitSource request, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<DeleteGitSourceOutcome>(async (tx, token) =>
        {
            var source = await sources.Find(tx.Session,
                GitRepositoryFilters.Source(request.TenantId, request.SourceId)).FirstOrDefaultAsync(token);
            if (source is null)
                return tx.Abort(new GitSourceNotFound());

            if (source.Activity == GitSourceActivity.Deleted)
                return tx.Commit(new GitSourceDeletionRecorded());

            if (source.Revision != request.ExpectedRevision)
                return tx.Abort(new GitSourceRevisionConflict());

            var owned = await inventory.CountDocumentsAsync(tx.Session,
                ManagedDeclarationFilters.SourceInventory(source.TenantId, source.Id), cancellationToken: token);
            if (owned != 0)
                return tx.Abort(new GitSourceDetachRequired(owned));

            var state = await states.Find(tx.Session, GitRepositoryFilters.State(source.TenantId, source.Id))
                .SingleAsync(token);
            if (source.Activity == GitSourceActivity.Detaching || state.CurrentRun is not null ||
                state.RequestedGeneration != state.CompletedGeneration)
                return tx.Abort(new GitSourceBusy());

            // Every ownership claim writes the same synchronization root. This write serializes
            // the empty-inventory observation with concurrent execution and new ownership.
            await states.ReplaceOneAsync(tx.Session, GitRepositoryFilters.State(source.TenantId, source.Id),
                StopWork(state), cancellationToken: token);
            await sources.ReplaceOneAsync(tx.Session, GitRepositoryFilters.Source(source.TenantId, source.Id),
                source.Delete(owned), cancellationToken: token);
            return tx.Commit(new GitSourceDeletionRecorded());
        }, ct);

    private Task SupersedeAsync(
        RepositorySource source,
        GitSynchronizationState state,
        DateTime now,
        IClientSessionHandle session,
        CancellationToken ct) => state.CurrentRun is { } current
        ? runs.UpdateOneAsync(session, GitRepositoryFilters.Run(source.TenantId, source.Id, current.RunId),
            Builders<GitReconciliationRun>.Update.Set(item => item.State,
                new GitRunFinished(GitRunCompletion.Superseded, now)), cancellationToken: ct)
        : Task.CompletedTask;

    private static GitSynchronizationState StopWork(GitSynchronizationState state) => state with
    {
        Fence = checked(state.Fence + 1),
        MutationVersion = checked(state.MutationVersion + 1),
        CurrentRun = null,
        CompletedGeneration = state.RequestedGeneration,
        Pending = new(new(GitRunTrigger.Recovery, null), null)
    };
}
