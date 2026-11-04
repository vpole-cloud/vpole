using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Events;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.GitOps.Application.Persistence;

internal enum GitSignalKind { Repository, Dependency, Poll }

internal sealed record ScheduleGitSource(
    GitSourceIdentity Source,
    string Key,
    string InputHash,
    GitSignalKind Kind,
    DateTime SchedulingTime);

public sealed class GitSourceScheduling(
    IMongoCollection<RepositorySource> sources,
    IMongoCollection<GitSynchronizationState> states,
    IMongoCollection<GitReconciliationRun> runs,
    IMongoCollection<GitSourceRequestReceipt> receipts,
    IMongoCollection<ManagedDeclaration> inventory,
    IMongoCollection<GitAcceptedReference> references,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    TimeProvider clock,
    IOptions<GitOpsWorkerOptions> options) : IGitSourceScheduler
{
    public async Task RequestRepositorySyncAsync(RepositorySyncSignal signal, CancellationToken cancellationToken)
    {
        var hash = Hash(signal);
        using var cursor = await sources.Find(GitSchedulingFilters.Repository(signal))
            .Project(item => new GitSourceIdentity(item.TenantId, item.Id)).ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
            foreach (var source in cursor.Current)
                await ScheduleAsync(new(source,
                        $"repository/{signal.SignalId}",
                        hash,
                        GitSignalKind.Repository,
                        clock.GetUtcNow().UtcDateTime),
                    cancellationToken);
    }

    public async Task WakeDependencyAsync(GitDependencyWakeSignal signal, CancellationToken cancellationToken)
    {
        var owners = await inventory.Find(ManagedDeclarationFilters.Resource(signal.TenantId,
            signal.Address.Kind,
            signal.ResourceId)).Project(item => item.SourceId).ToListAsync(cancellationToken);
        var consumers = await references.Find(GitAcceptedReferenceFilters.Target(signal.TenantId,
            signal.Address.Kind,
            signal.ResourceId)).Project(item => item.Source.SourceId).ToListAsync(cancellationToken);
        foreach (var source in owners.Concat(consumers).Distinct())
            await ScheduleAsync(new(new(signal.TenantId, source),
                    $"dependency/{signal.SignalId}",
                    Hash(signal),
                    GitSignalKind.Dependency,
                    clock.GetUtcNow().UtcDateTime),
                cancellationToken);
    }

    public async Task<IReadOnlyList<GitSourceIdentity>> RecoverableSourcesAsync(CancellationToken ct)
    {
        var limit = options.Value.BatchSize;
        var result = new List<GitSourceIdentity>();
        var dueRuns = await runs.Find(GitSchedulingFilters.Recoverable()).SortBy(item => item.CreatedAtUtc)
            .Limit(limit).Project(item => item.Stamp.Source).ToListAsync(ct);
        result.AddRange(dueRuns);
        if (result.Count < limit)
        {
            var pending = await states.Find(GitSchedulingFilters.Pending()).SortBy(item => item.Id)
                .Limit(limit - result.Count).Project(item => item.Source).ToListAsync(ct);
            result.AddRange(pending);
        }

        if (result.Count < limit)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var polls = await states.Find(GitSchedulingFilters.PollDue(now)).SortBy(item => item.NextPollAtUtc)
                .Limit(limit - result.Count).ToListAsync(ct);
            foreach (var state in polls)
                if (await ScheduleAsync(new(state.Source,
                            $"poll/{state.NextPollAtUtc.Ticks}",
                            state.NextPollAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            GitSignalKind.Poll,
                            now),
                        ct))
                    result.Add(state.Source);
        }

        return result.Distinct().Take(limit).ToArray();
    }

    private async Task<bool> ScheduleAsync(ScheduleGitSource request, CancellationToken ct)
    {
        try
        {
            return await ScheduleOnceAsync(request, ct);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIdIndex(exception,
                                                        receipts.CollectionNamespace.CollectionName))
        {
            return await ScheduleOnceAsync(request, ct);
        }
    }

    private Task<bool> ScheduleOnceAsync(ScheduleGitSource request, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<bool>(async (tx, token) =>
            {
                var identity = request.Source;
                var source = await sources.Find(tx.Session,
                    GitRepositoryFilters.Source(identity.TenantId, identity.SourceId)).FirstOrDefaultAsync(token);
                if (source is null || source.Activity != GitSourceActivity.Active)
                    return tx.Abort(false);

                var key = $"{identity.TenantId}/{identity.SourceId}/{request.Key}";
                var receipt = await receipts.Find(tx.Session, GitRepositoryFilters.Request(key))
                    .FirstOrDefaultAsync(token);
                if (receipt is not null)
                {
                    if (receipt.InputHash != request.InputHash)
                        throw new InvalidOperationException("A server scheduling signal changed its immutable input.");
                    return tx.Commit(false);
                }

                var state = await states.Find(tx.Session,
                    GitRepositoryFilters.State(identity.TenantId, identity.SourceId)).SingleAsync(token);
                if (request.Kind == GitSignalKind.Poll && state.NextPollAtUtc > request.SchedulingTime)
                    return tx.Abort(false);

                var now = await new GitDatabaseClock(sources).NowAsync(identity, tx.Session, token);
                var initiator = new GitRunInitiator(request.Kind switch
                {
                    GitSignalKind.Repository => GitRunTrigger.Webhook,
                    GitSignalKind.Dependency => GitRunTrigger.Dependency,
                    GitSignalKind.Poll => GitRunTrigger.Poll,
                    _ => throw new ArgumentOutOfRangeException(nameof(request))
                },
                    null);
                GitReconciliationRun? previous = null;
                if (request.Kind == GitSignalKind.Dependency && state.CurrentRun is null &&
                    state.RequestedGeneration == state.CompletedGeneration && state.LatestRunId is { } latestId)
                    previous = await runs.Find(tx.Session,
                        GitRepositoryFilters.Run(identity.TenantId, identity.SourceId, latestId)).SingleAsync(token);

                var eligible = request.Kind != GitSignalKind.Dependency || previous is
                {
                    Input: GitInputAccepted,
                    State: GitRunFinished { Completion: GitRunCompletion.Blocked or GitRunCompletion.Failed }
                } && previous.Stamp.ConfigurationRevision == source.Revision;
                if (eligible)
                {
                    state = state.RequestSync(new(initiator, previous?.Id));
                    if (previous is not null)
                    {
                        state = state.BeginRun(ObjectId.GenerateNewId().ToString());
                        var stamp = new GitRunStamp(identity,
                            state.CurrentRun!.RunId,
                            source.Revision,
                            state.CurrentRun.Generation,
                            state.Fence);
                        var retry = GitReconciliationRunFactory.Create(stamp,
                            source.Repository,
                            initiator,
                            now,
                            previous.Input,
                            previous.Id);
                        await runs.InsertOneAsync(tx.Session, retry, cancellationToken: token);
                    }

                    if (request.Kind == GitSignalKind.Poll)
                        state = state with { NextPollAtUtc = request.SchedulingTime + source.Sync.PollInterval };
                    await states.ReplaceOneAsync(tx.Session,
                        GitRepositoryFilters.State(identity.TenantId, identity.SourceId),
                        state,
                        cancellationToken: token);
                    await outbox(tx.Session,
                        [OutboxWrite.Create(new GitSourceWorkRequested(source.Id, EventCore.Create(source.TenantId)))],
                        token);
                }

                // Even an ignored dependency delivery is recorded: redelivery must not supersede a newer Sync later.
                await receipts.InsertOneAsync(tx.Session,
                    new(key,
                        identity.TenantId,
                        identity.SourceId,
                        request.Key,
                        request.InputHash,
                        state.RequestedGeneration,
                        initiator,
                        now),
                    cancellationToken: token);
                return tx.Commit(eligible);
            },
            ct);

    private static string Hash<T>(T value) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
