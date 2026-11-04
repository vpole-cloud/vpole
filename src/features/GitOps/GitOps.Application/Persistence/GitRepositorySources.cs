using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Events;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.GitOps.Application.Persistence;

public sealed class GitRepositorySources(
    IMongoCollection<RepositorySource> sources,
    IMongoCollection<GitSynchronizationState> states,
    IMongoCollection<GitReconciliationRun> runs,
    IMongoCollection<GitSourceRequestReceipt> requests,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
{
    public Task<RepositorySource?> LoadAsync(string tenant, string id, CancellationToken ct) =>
        sources.Find(GitRepositoryFilters.Source(tenant, id)).FirstOrDefaultAsync(ct)!;

    public Task<GitSynchronizationState> StateAsync(string tenant, string id, CancellationToken ct) =>
        states.Find(GitRepositoryFilters.State(tenant, id)).SingleAsync(ct);

    public Task<GitReconciliationRun?> RunAsync(string tenant, string source, string run, CancellationToken ct) =>
        runs.Find(GitRepositoryFilters.Run(tenant, source, run)).FirstOrDefaultAsync(ct)!;

    public async Task<CreateGitRepositorySourceOutcome> CreateAsync(
        CreateGitRepositorySource request,
        CancellationToken ct)
    {
        var hash = Hash(new { request.Repository, request.Sync });
        var candidate = RepositorySourceFactory.Create(new(request.TenantId, ObjectId.GenerateNewId().ToString()),
            request.Repository,
            request.Sync,
            request.Audit,
            request.CreationKey,
            hash,
            request.DisplayUrl);
        var creation = GitRepositoryFilters.Creation(request.TenantId, request.CreationKey);
        try
        {
            return await transactions.InvokeOutcomeAsync<CreateGitRepositorySourceOutcome>(async (tx, token) =>
                {
                    var existing = await sources.Find(tx.Session, creation).FirstOrDefaultAsync(token);
                    if (existing is not null)
                        return existing.CreationHash == hash
                            ? tx.Commit(existing)
                            : tx.Abort(new GitSourceIdempotencyConflict());

                    var state = GitSynchronizationStateFactory.Create(new(candidate.TenantId, candidate.Id),
                            candidate.Audit.CreatedAtUtc + candidate.Sync.PollInterval)
                        .RequestSync(new(new(GitRunTrigger.Manual, candidate.Audit.CreatedByUserId), null));
                    await sources.InsertOneAsync(tx.Session, candidate, cancellationToken: token);
                    await states.InsertOneAsync(tx.Session, state, cancellationToken: token);
                    await WakeAsync(candidate, tx.Session, token);
                    return tx.Commit(candidate);
                },
                ct);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(exception,
                                                        RepositoryCollections.CreationIndex,
                                                        sources.CollectionNamespace.CollectionName))
        {
            var existing = await sources.Find(creation).SingleAsync(ct);
            return existing.CreationHash == hash ? existing : new GitSourceIdempotencyConflict();
        }
    }

    public async Task<RequestGitSourceSyncOutcome> RequestSyncAsync(GitSourceCommand request, CancellationToken ct)
    {
        var outcome = await RequestAsync(request, null, ct);
        return outcome switch
        {
            GitSourceScheduled scheduled => scheduled,
            GitSourceNotFound missing => missing,
            GitSourceRevisionConflict stale => stale,
            GitSourceIdempotencyConflict conflict => conflict,
            GitSourceBusy or GitRetryNotAvailable =>
                throw new InvalidOperationException("Sync cannot be rejected by a retry precondition.")
        };
    }

    public Task<RetryGitSourceOutcome> RetryAsync(RetryGitSourceCommand request, CancellationToken ct) =>
        RequestAsync(request.Source, request.RunId, ct);

    private async Task<RetryGitSourceOutcome> RequestAsync(
        GitSourceCommand request,
        string? retryOfRunId,
        CancellationToken ct)
    {
        var key = $"{request.TenantId}/{request.SourceId}/{request.RequestKey}";
        var hash = Hash(new { request.ExpectedRevision, RetryOfRunId = retryOfRunId });

        async Task<RetryGitSourceOutcome> AttemptAsync() =>
            await transactions.InvokeOutcomeAsync<RetryGitSourceOutcome>(async (tx, token) =>
                {
                    var source = await sources.Find(tx.Session,
                        GitRepositoryFilters.Source(request.TenantId,
                            request.SourceId)).FirstOrDefaultAsync(token);
                    if (source is null)
                        return tx.Abort(new GitSourceNotFound());

                    var receipt = await requests.Find(tx.Session, GitRepositoryFilters.Request(key))
                        .FirstOrDefaultAsync(token);
                    if (receipt is not null)
                        return receipt.InputHash == hash
                            ? tx.Commit(new GitSourceScheduled(source.Id, receipt.Generation))
                            : tx.Abort(new GitSourceIdempotencyConflict());

                    if (source.Revision != request.ExpectedRevision)
                        return tx.Abort(new GitSourceRevisionConflict());

                    var state = await states.Find(tx.Session, GitRepositoryFilters.State(source.TenantId, source.Id))
                        .SingleAsync(token);
                    GitReconciliationRun? previous = null;
                    if (retryOfRunId is not null)
                    {
                        if (state.CurrentRun is not null || state.RequestedGeneration > state.CompletedGeneration)
                            return tx.Abort(new GitSourceBusy());

                        previous = await runs.Find(tx.Session,
                            GitRepositoryFilters.Run(source.TenantId,
                                source.Id,
                                retryOfRunId)).FirstOrDefaultAsync(token);
                        if (previous is null || state.LatestRunId != previous.Id ||
                            previous.Stamp.ConfigurationRevision != source.Revision || previous.Commit is null ||
                            previous.State is not GitRunFinished
                            {
                                Completion: GitRunCompletion.Blocked or GitRunCompletion.Failed
                            })
                            return tx.Abort(new GitRetryNotAvailable());
                    }

                    var now = await new GitDatabaseClock(sources).NowAsync(
                        new(source.TenantId, source.Id),
                        tx.Session,
                        token);
                    var next = state.RequestSync(new(request.Initiator, retryOfRunId));
                    if (previous is not null)
                    {
                        next = next.BeginRun(ObjectId.GenerateNewId().ToString());
                        var stamp = new GitRunStamp(new(source.TenantId, source.Id),
                            next.CurrentRun!.RunId,
                            source.Revision,
                            next.CurrentRun.Generation,
                            next.Fence);
                        var retry = GitReconciliationRunFactory.Create(stamp,
                            source.Repository,
                            request.Initiator,
                            now,
                            previous.Input,
                            previous.Id);
                        await runs.InsertOneAsync(tx.Session, retry, cancellationToken: token);
                    }

                    await states.ReplaceOneAsync(tx.Session,
                        GitRepositoryFilters.State(source.TenantId, source.Id),
                        next,
                        cancellationToken: token);
                    await requests.InsertOneAsync(tx.Session,
                        new(key,
                            source.TenantId,
                            source.Id,
                            request.RequestKey,
                            hash,
                            next.RequestedGeneration,
                            request.Initiator,
                            now),
                        cancellationToken: token);
                    await WakeAsync(source, tx.Session, token);
                    return tx.Commit(new GitSourceScheduled(source.Id, next.RequestedGeneration));
                },
                ct);

        try
        {
            return await AttemptAsync();
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIdIndex(exception,
                                                        requests.CollectionNamespace.CollectionName))
        {
            // A duplicate request raced on the same source. Its transaction has already committed.
            return await AttemptAsync();
        }
    }

    public Task<ChangeGitSourcePolicyOutcome> ChangePolicyAsync(
        ChangeGitSourcePolicy request,
        CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<ChangeGitSourcePolicyOutcome>(async (tx, token) =>
            {
                var source = await sources.Find(tx.Session,
                    GitRepositoryFilters.Source(request.TenantId,
                        request.SourceId)).FirstOrDefaultAsync(token);
                if (source is null)
                    return tx.Abort(new GitSourceNotFound());
                if (source.Revision != request.ExpectedRevision)
                    return tx.Abort(new GitSourceRevisionConflict());
                if (source.Sync == request.Sync)
                    return tx.Commit(source);

                var now = await new GitDatabaseClock(sources).NowAsync(new(source.TenantId, source.Id),
                    tx.Session,
                    token);
                var state = await states.Find(tx.Session, GitRepositoryFilters.State(source.TenantId, source.Id))
                    .SingleAsync(token);
                if (state.CurrentRun is { } current)
                    await runs.UpdateOneAsync(tx.Session,
                        GitRepositoryFilters.Run(source.TenantId, source.Id, current.RunId),
                        Builders<GitReconciliationRun>.Update.Set(item => item.State,
                            new GitRunFinished(GitRunCompletion.Superseded, now)),
                        cancellationToken: token);

                var next = source with { Sync = request.Sync, Revision = checked(source.Revision + 1) };
                var nextState = (state with
                {
                    Fence = checked(state.Fence + 1),
                    CurrentRun = null,
                    CompletedGeneration = state.RequestedGeneration,
                    NextPollAtUtc = now + next.Sync.PollInterval
                }).RequestSync(new(new(GitRunTrigger.Recovery, null), null));
                await sources.ReplaceOneAsync(tx.Session,
                    GitRepositoryFilters.Source(source.TenantId, source.Id),
                    next,
                    cancellationToken: token);
                await states.ReplaceOneAsync(tx.Session,
                    GitRepositoryFilters.State(source.TenantId, source.Id),
                    nextState,
                    cancellationToken: token);
                await WakeAsync(next, tx.Session, token);
                return tx.Commit(next);
            },
            ct);

    internal Task WakeAsync(RepositorySource source, IClientSessionHandle session, CancellationToken ct) =>
        outbox(session,
            [
                OutboxWrite.Create(new GitSourceWorkRequested(source.Id,
                    EventCore.Create(source.TenantId)))
            ],
            ct);

    private static string Hash<T>(T value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
