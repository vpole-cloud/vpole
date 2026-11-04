using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

public sealed record GitRunHandle(
    GitRunStamp Stamp,
    GitClaimIdentity Identity);

public sealed record GitWorkClaim(
    RepositorySource Source,
    GitReconciliationRun Run,
    GitRunHandle Handle,
    string? PreviouslyAppliedCommit);

public sealed record GitNoWorkAvailable;

public union ClaimGitRunOutcome(
    GitWorkClaim,
    GitNoWorkAvailable);

internal sealed record GitRunTransition(
    GitReconciliationRun Run,
    GitSynchronizationState State);

public sealed class GitRunStore(
    IMongoCollection<RepositorySource> sources,
    IMongoCollection<GitSynchronizationState> states,
    IMongoCollection<GitReconciliationRun> runs,
    IMongoCollection<ManagedDeclaration> inventory,
    IMongoCollection<GitOperationReceipt> receipts,
    MongoRunTransaction transactions,
    IOptions<GitOpsWorkerOptions> options)
{
    private readonly GitDatabaseClock clock = new(sources);

    public Task<ClaimGitRunOutcome> ClaimAsync(GitSourceIdentity sourceId, string worker, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<ClaimGitRunOutcome>(async (tx, token) =>
            {
                var source = await sources.Find(tx.Session,
                    GitRepositoryFilters.Source(sourceId.TenantId, sourceId.SourceId)).FirstOrDefaultAsync(token);
                if (source is null || source.Activity != GitSourceActivity.Active)
                    return tx.Abort(new GitNoWorkAvailable());

                var state = await states.Find(tx.Session,
                    GitRepositoryFilters.State(source.TenantId, source.Id)).SingleAsync(token);
                var now = await clock.NowAsync(sourceId, tx.Session, token);
                GitReconciliationRun run;
                string? previouslyApplied = null;
                if (state.CurrentRun is { } active)
                {
                    run = await runs.Find(tx.Session,
                        GitRepositoryFilters.Run(source.TenantId, source.Id, active.RunId)).SingleAsync(token);
                    var attempt = AvailableAttempt(run.State, now);
                    if (attempt is null || run.Stamp.ConfigurationRevision != source.Revision)
                        return tx.Abort(new GitNoWorkAvailable());

                    if (run.Input is not GitInputAccepted && run.RetryOfRunId is null)
                    {
                        var previous = await runs.Find(tx.Session, GitRepositoryFilters.PreviousRuns(run.Stamp))
                            .SortByDescending(item => item.Stamp.Generation).FirstOrDefaultAsync(token);
                        if (previous?.Stamp.ConfigurationRevision == source.Revision && previous.State is GitRunFinished
                            {
                                Completion: GitRunCompletion.ConfigurationApplied or GitRunCompletion.AlreadyCurrent
                            })
                            previouslyApplied = previous.Commit;
                    }

                    run = run.Claim(new(worker, attempt.Value, now.AddSeconds(options.Value.LeaseSeconds)), now);
                    await runs.ReplaceOneAsync(tx.Session,
                        GitRepositoryFilters.Run(source.TenantId, source.Id, run.Id),
                        run,
                        cancellationToken: token);
                    // Both claim and provider transactions write the synchronization document. A concurrent
                    // configuration update, claimant, or mutation cannot commit against this snapshot.
                    state = state with { MutationVersion = checked(state.MutationVersion + 1) };
                }
                else
                {
                    if (state.RequestedGeneration <= state.CompletedGeneration)
                        return tx.Abort(new GitNoWorkAvailable());

                    var latest = state.LatestRunId is { } latestId
                        ? await runs.Find(tx.Session, GitRepositoryFilters.Run(source.TenantId, source.Id, latestId))
                            .SingleAsync(token)
                        : null;
                    if (latest?.Stamp.ConfigurationRevision == source.Revision && latest.State is GitRunFinished
                        {
                            Completion: GitRunCompletion.ConfigurationApplied or GitRunCompletion.AlreadyCurrent
                        })
                        previouslyApplied = latest.Commit;

                    GitRunInput input = new GitInputUnresolved();
                    if (state.Pending.RetryOfRunId is { } retryId)
                    {
                        if (latest is null || latest.Id != retryId || latest.Commit is null ||
                            latest.Stamp.ConfigurationRevision != source.Revision)
                            throw new InvalidOperationException("A pending retry lost its admitted immutable input.");
                        input = latest.Input;
                    }

                    state = state.BeginRun(ObjectId.GenerateNewId().ToString());
                    var stamp = new GitRunStamp(sourceId,
                        state.CurrentRun!.RunId,
                        source.Revision,
                        state.CurrentRun.Generation,
                        state.Fence);
                    run = GitReconciliationRunFactory.Create(stamp,
                            source.Repository,
                            state.Pending.Initiator,
                            now,
                            input,
                            state.Pending.RetryOfRunId)
                        .Claim(new(worker, 1, now.AddSeconds(options.Value.LeaseSeconds)), now);
                    if (run.Commit is { } commit)
                        state = state.Observe(commit);
                    if (run.Input is GitInputAccepted accepted)
                        state = state.Attempt(accepted.Snapshot.Commit);
                    await runs.InsertOneAsync(tx.Session, run, cancellationToken: token);
                }

                await states.ReplaceOneAsync(tx.Session,
                    GitRepositoryFilters.State(source.TenantId, source.Id),
                    state,
                    cancellationToken: token);
                var lease = ((GitRunClaimed)run.State).Lease;
                return tx.Commit(new GitWorkClaim(source,
                    run,
                    new(run.Stamp, new(lease.WorkerId, lease.Attempt)),
                    previouslyApplied));
            },
            ct);

    public Task<bool> HeartbeatAsync(GitRunHandle handle, CancellationToken ct) =>
        TransitionAsync(handle,
            (run, state, now) => new(run with
            {
                State = new GitRunClaimed(new(handle.Identity.WorkerId,
                        handle.Identity.Attempt,
                        now.AddSeconds(options.Value.LeaseSeconds)))
            },
                state),
            ct);

    public Task<bool> PinAsync(GitRunHandle handle, string commit, CancellationToken ct) =>
        TransitionAsync(handle,
            (run, state, _) =>
            {
                var pinned = run.Pin(commit);
                return new(pinned, state.Observe(pinned.Commit!));
            },
            ct);

    public Task<bool> CompleteAsync(
        GitRunHandle handle,
        GitRunCompletion completion,
        ImmutableArray<GitRunDiagnostic> diagnostics,
        CancellationToken ct) => TransitionAsync(handle,
        (run, state, now) => new(
            run.Complete(handle.Identity, completion, now) with { Diagnostics = diagnostics },
            state.CompleteRun(run.Stamp, completion)),
        ct);

    public Task<bool> DelayAsync(GitRunHandle handle, GitRunDiagnostic diagnostic, CancellationToken ct) =>
        TransitionAsync(handle,
            (run, state, now) =>
            {
                if (run.ConsecutiveFailures + 1 >= options.Value.MaximumConsecutiveFailures)
                    return new(run.Complete(handle.Identity, GitRunCompletion.Failed, now) with
                    {
                        ConsecutiveFailures = run.ConsecutiveFailures + 1,
                        Diagnostics = [diagnostic]
                    },
                        state.CompleteRun(run.Stamp, GitRunCompletion.Failed));

                return new(run.Delay(handle.Identity,
                        now,
                        now + options.Value.RetryDelay(run.ConsecutiveFailures + 1),
                        diagnostic),
                    state);
            },
            ct);

    public Task<bool> AcceptAsync(GitRunHandle handle, GitAcceptedSnapshot snapshot, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<bool>(async (tx, token) =>
            {
                var run = await TouchAsync(handle, tx.Session, token);
                if (run is null)
                    return tx.Abort(false);

                var state = await states.Find(tx.Session, GitRepositoryFilters.ActiveState(handle.Stamp))
                    .SingleAsync(token);
                var accepted = run.Accept(snapshot);
                var desired = snapshot.Resources.Select(item => (item.Kind, item.Name)).ToHashSet();
                var owned = await inventory.Find(tx.Session,
                    ManagedDeclarationFilters.SourceInventory(
                        handle.Stamp.Source.TenantId,
                        handle.Stamp.Source.SourceId)).ToListAsync(token);
                foreach (var item in owned)
                    await inventory.UpdateOneAsync(tx.Session,
                        ManagedDeclarationFilters.Address(
                            item.TenantId,
                            new(item.Kind, item.Name)),
                        Builders<ManagedDeclaration>.Update.Set(value => value.PendingRemoval,
                            !desired.Contains((item.Kind, item.Name))),
                        cancellationToken: token);

                return await SaveAsync(new(accepted, state.Attempt(snapshot.Commit)), handle, tx.Session, token)
                    ? tx.Commit(true)
                    : tx.Abort(false);
            },
            ct);

    // Call only inside the provider transaction. This is a write fence, not a preflight permission read.
    internal async Task<GitReconciliationRun?> TouchAsync(
        GitRunHandle handle,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        GitTransactionGuard.Require(session);
        var source = await sources.Find(session, GitRepositoryFilters.SourceRevision(handle.Stamp))
            .FirstOrDefaultAsync(ct);
        if (source is null || source.Activity != GitSourceActivity.Active)
            return null;

        var stateWrite = await states.UpdateOneAsync(session,
            GitRepositoryFilters.ActiveState(handle.Stamp),
            Builders<GitSynchronizationState>.Update.Inc(item => item.MutationVersion, 1),
            cancellationToken: ct);
        if (stateWrite.ModifiedCount != 1)
            return null;

        return await runs.FindOneAndUpdateAsync(session,
            GitRepositoryFilters.LiveClaim(handle.Stamp, handle.Identity),
            Builders<GitReconciliationRun>.Update.Inc(item => item.MutationVersion, 1),
            new FindOneAndUpdateOptions<GitReconciliationRun> { ReturnDocument = ReturnDocument.After },
            ct);
    }

    private Task<bool> TransitionAsync(
        GitRunHandle handle,
        Func<GitReconciliationRun, GitSynchronizationState, DateTime, GitRunTransition> transition,
        CancellationToken ct) => transactions.InvokeOutcomeAsync<bool>(async (tx, token) =>
        {
            var run = await TouchAsync(handle, tx.Session, token);
            if (run is null)
                return tx.Abort(false);

            var state = await states.Find(tx.Session, GitRepositoryFilters.ActiveState(handle.Stamp))
                .SingleAsync(token);
            var now = await clock.NowAsync(handle.Stamp.Source, tx.Session, token);
            // The lease can expire between the first fencing write and the server clock read.
            if (run.State is not GitRunClaimed claimed || claimed.Lease.ExpiresAtUtc <= now)
                return tx.Abort(false);

            return await SaveAsync(transition(run, state, now), handle, tx.Session, token)
                ? tx.Commit(true)
                : tx.Abort(false);
        },
        ct);

    private async Task<bool> SaveAsync(
        GitRunTransition transition,
        GitRunHandle handle,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (transition.Run.State is GitRunFinished { Completion: GitRunCompletion.ConfigurationApplied } &&
            !await HasCompleteReceiptsAsync(transition.Run, session, ct))
            return false;

        var written = await runs.ReplaceOneAsync(session,
            GitRepositoryFilters.LiveClaim(handle.Stamp, handle.Identity),
            transition.Run,
            cancellationToken: ct);
        if (written.MatchedCount != 1)
            return false;

        await states.ReplaceOneAsync(session,
            GitRepositoryFilters.ActiveState(handle.Stamp),
            transition.State,
            cancellationToken: ct);
        return true;
    }

    private async Task<bool> HasCompleteReceiptsAsync(
        GitReconciliationRun run,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (run.Input is not GitInputAccepted accepted)
            return false;

        var stored = await receipts.Find(session,
            GitRepositoryFilters.Receipts(run.Stamp.Source.TenantId,
                run.Stamp.Source.SourceId,
                run.Id)).ToListAsync(ct);
        var byOperation = stored.ToDictionary(item => (item.Kind, item.Name, item.Phase));
        var resources = accepted.Snapshot.Resources.ToDictionary(item => (item.Kind, item.Name));
        return accepted.Snapshot.Operations.All(operation =>
            byOperation.TryGetValue((operation.Kind, operation.Name, operation.Phase.ToString()), out var receipt) &&
            receipt.Blocker is null && receipt.Fence == run.Stamp.Fence &&
            receipt.ResourceId == operation.ResourceId &&
            receipt.DesiredHash == resources[(operation.Kind, operation.Name)].Hash);
    }

    private static long? AvailableAttempt(GitRunState state, DateTime now) => state switch
    {
        GitRunWaiting waiting => checked(waiting.LastAttempt + 1),
        GitRunDelayed delayed when delayed.NextAttemptAtUtc <= now => checked(delayed.LastAttempt + 1),
        GitRunClaimed claimed when claimed.Lease.ExpiresAtUtc <= now => checked(claimed.Lease.Attempt + 1),
        _ => null
    };
}
