// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Pole.Architecture;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Events;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.GitOps.Application.Sources;

internal enum GitSchedulingEligibility { Active, AdmittedLifecycle }

internal enum GitSignalKind { Repository, Dependency, Poll }

internal sealed record ScheduleGitSource(
    GitSourceIdentity Source,
    string Key,
    string InputHash,
    GitSignalKind Kind,
    DateTime SchedulingTime
);

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
    IOptions<GitOpsWorkerOptions> options
) : IGitSourceScheduler
{
    public async Task RequestRepositorySyncAsync(RepositorySyncSignal signal, CancellationToken cancellationToken)
    {
        var hash = Hash(signal);
        using var cursor = await sources.Find(GitSchedulingFilters.Repository(signal))
            .Project(item => new GitSourceIdentity(item.TenantId, item.Id))
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
            foreach (var source in cursor.Current)
                await ScheduleAsync(
                    new ScheduleGitSource(
                        source,
                        $"repository/{signal.SignalId}",
                        hash,
                        GitSignalKind.Repository,
                        clock.GetUtcNow().UtcDateTime),
                    cancellationToken);
    }

    public async Task WakeDependencyAsync(GitDependencyWakeSignal signal, CancellationToken cancellationToken)
    {
        var owners = await inventory
            .Find(ManagedDeclarationFilters.Resource(signal.TenantId, signal.Address.Kind, signal.ResourceId))
            .Project(item => item.SourceId)
            .ToListAsync(cancellationToken);
        var consumers = await references
            .Find(GitAcceptedReferenceFilters.Target(signal.TenantId, signal.Address.Kind, signal.ResourceId))
            .Project(item => item.Source.SourceId)
            .ToListAsync(cancellationToken);
        foreach (var source in owners.Concat(consumers).Distinct())
            await ScheduleAsync(
                new ScheduleGitSource(
                    new GitSourceIdentity(signal.TenantId, source),
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
        var dueRuns = await ActiveSourceDocuments(
                runs,
                GitSchedulingFilters.Recoverable(),
                item => item.Stamp.Source,
                GitSchedulingEligibility.AdmittedLifecycle)
            .SortBy(item => item.CreatedAtUtc)
            .Limit(limit)
            .Project(item => item.Stamp.Source)
            .ToListAsync(ct);
        result.AddRange(dueRuns);
        if (result.Count < limit)
        {
            var pending = await ActiveSourceDocuments(states, GitSchedulingFilters.Pending(), item => item.Source)
                .SortBy(item => item.Id)
                .Limit(limit - result.Count)
                .Project(item => item.Source)
                .ToListAsync(ct);
            result.AddRange(pending);
        }

        if (result.Count < limit)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var polls = await ActiveSourceDocuments(states, GitSchedulingFilters.PollDue(now), item => item.Source)
                .SortBy(item => item.NextPollAtUtc)
                .Limit(limit - result.Count)
                .ToListAsync(ct);
            foreach (var state in polls)
                if (await ScheduleAsync(
                        new ScheduleGitSource(
                            state.Source,
                            $"poll/{state.NextPollAtUtc.Ticks}",
                            state.NextPollAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                            GitSignalKind.Poll,
                            now),
                        ct))
                    result.Add(state.Source);
        }

        return result.Distinct().Take(limit).ToArray();
    }

    // Filter before Limit: inactive sources must not starve an eligible source later in the scan.
    [ViolateArchitecture(
        ArchitectureRules.RawBson,
        "The generic scheduling join uses serializer-mapped paths, tenant correlation and lifecycle " +
        "eligibility before Limit.")]
    private IAggregateFluent<T> ActiveSourceDocuments<T>(
        IMongoCollection<T> documents,
        FilterDefinition<T> predicate,
        Expression<Func<T, GitSourceIdentity>> identity,
        GitSchedulingEligibility eligibility = GitSchedulingEligibility.Active)
    {
        var registry = BsonSerializer.SerializerRegistry;
        var path = new ExpressionFieldDefinition<T, GitSourceIdentity>(identity)
            .Render(new RenderArgs<T>(registry.GetSerializer<T>(), registry))
            .FieldName;
        var allowed = GitSchedulingFilters.EligibleSources(eligibility);
        var active = allowed.Render(
            new RenderArgs<RepositorySource>(registry.GetSerializer<RepositorySource>(), registry));
        var eligible
            = MongoFields.Field<GitSchedulingLookup, RepositorySource[]>(result => result.EligibleSchedulingSource);
        var sourceIdField = MongoFields.Field<GitSourceIdentity, string>(source => source.SourceId);
        var identityTenantField = MongoFields.Field<GitSourceIdentity, string>(source => source.TenantId);
        var sourceTenantField = MongoFields.Field<RepositorySource, string>(source => source.TenantId);
        var sourceActivityField = MongoFields.Field<RepositorySource, GitSourceActivity>(source => source.Activity);
        var lookup = new BsonDocument(
            "$lookup",
            new BsonDocument
            {
                { "from", sources.CollectionNamespace.CollectionName },
                { "localField", $"{path}.{sourceIdField}" },
                { "foreignField", MongoFields.Field<RepositorySource, string>(source => source.Id) },
                { "pipeline", new BsonArray { new BsonDocument("$match", active) } },
                { "as", eligible },
            });
        var sameTenant = new BsonDocument(
            "$expr",
            new BsonDocument(
                "$eq",
                new BsonArray
                {
                    $"${path}.{identityTenantField}",
                    new BsonDocument("$arrayElemAt", new BsonArray { $"${eligible}.{sourceTenantField}", 0 }),
                }));
        var candidates = documents.Aggregate()
            .Match(predicate)
            .AppendStage<T>(lookup)
            .AppendStage<T>(
                new BsonDocument(
                    "$match",
                    new BsonDocument
                    {
                        { $"{eligible}.0", new BsonDocument("$exists", true) },
                        { "$expr", sameTenant["$expr"] },
                    }));
        if (eligibility == GitSchedulingEligibility.AdmittedLifecycle)
            candidates = candidates.AppendStage<T>(
                new BsonDocument(
                    "$match",
                    new BsonDocument(
                        "$or",
                        new BsonArray
                        {
                            new BsonDocument($"{eligible}.{sourceActivityField}", (int)GitSourceActivity.Active),
                            GitRepositoryFilters.LifecycleInput()
                                .Render(
                                    new RenderArgs<GitReconciliationRun>(
                                        registry.GetSerializer<GitReconciliationRun>(),
                                        registry)),
                        })));
        return candidates.AppendStage<T>(new BsonDocument("$unset", eligible));
    }

    private async Task<bool> ScheduleAsync(ScheduleGitSource request, CancellationToken ct)
    {
        try
        {
            return await ScheduleOnceAsync(request, ct);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIdIndex(
                                                        exception,
                                                        receipts.CollectionNamespace.CollectionName))
        {
            return await ScheduleOnceAsync(request, ct);
        }
    }

    private Task<bool> ScheduleOnceAsync(ScheduleGitSource request, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<bool>(
            async (tx, token) =>
            {
                var identity = request.Source;
                var source = await sources.Find(
                        tx.Session,
                        GitRepositoryFilters.Source(identity.TenantId, identity.SourceId))
                    .FirstOrDefaultAsync(token);
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

                var state = await states.Find(
                        tx.Session,
                        GitRepositoryFilters.State(identity.TenantId, identity.SourceId))
                    .SingleAsync(token);
                if (request.Kind == GitSignalKind.Poll && state.NextPollAtUtc > request.SchedulingTime)
                    return tx.Abort(false);

                var now = await new GitDatabaseClock(sources).NowAsync(identity, tx.Session, token);
                var initiator = new GitRunInitiator(
                    request.Kind switch
                    {
                        GitSignalKind.Repository => GitRunTrigger.Webhook,
                        GitSignalKind.Dependency => GitRunTrigger.Dependency,
                        GitSignalKind.Poll => GitRunTrigger.Poll,
                        _ => throw new ArgumentOutOfRangeException(nameof(request)),
                    },
                    null);
                GitReconciliationRun? previous = null;
                if (request.Kind == GitSignalKind.Dependency && state.CurrentRun is null &&
                    state.RequestedGeneration == state.CompletedGeneration && state.LatestRunId is { } latestId)
                    previous = await runs.Find(
                            tx.Session,
                            GitRepositoryFilters.Run(identity.TenantId, identity.SourceId, latestId))
                        .SingleAsync(token);

                var eligible = request.Kind != GitSignalKind.Dependency || (previous is
                {
                    Input: GitInputAccepted,
                    State: GitRunFinished { Completion: GitRunCompletion.Blocked or GitRunCompletion.Failed },
                } && previous.Stamp.ConfigurationRevision == source.Revision);
                if (eligible)
                {
                    state = state.RequestSync(new GitPendingRequest(initiator, previous?.Id));
                    if (previous is not null)
                    {
                        state = state.BeginRun(ObjectId.GenerateNewId().ToString());
                        var stamp = new GitRunStamp(
                            identity,
                            state.CurrentRun!.RunId,
                            source.Revision,
                            state.CurrentRun.Generation,
                            state.Fence);
                        var retry = GitReconciliationRunFactory.Create(
                            stamp,
                            source.Repository,
                            initiator,
                            now,
                            previous.Input,
                            previous.Id);
                        await runs.InsertOneAsync(tx.Session, retry, cancellationToken: token);
                    }

                    if (request.Kind == GitSignalKind.Poll)
                        state = state with { NextPollAtUtc = request.SchedulingTime + source.Sync.PollInterval };
                    await states.ReplaceOneAsync(
                        tx.Session,
                        GitRepositoryFilters.State(identity.TenantId, identity.SourceId),
                        state,
                        cancellationToken: token);
                    await outbox(
                        tx.Session,
                        [OutboxWrite.Create(new GitSourceWorkRequested(source.Id, EventCore.Create(source.TenantId)))],
                        token);
                }

                // Even an ignored dependency delivery is recorded: redelivery must not supersede a newer Sync later.
                await receipts.InsertOneAsync(
                    tx.Session,
                    new GitSourceRequestReceipt(
                        key,
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

internal sealed record GitSchedulingLookup(
    RepositorySource[] EligibleSchedulingSource
);
