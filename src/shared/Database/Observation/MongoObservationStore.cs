using Pole.Observation;
using MongoDB.Driver;

namespace Pole.Database.Observation;

/// <summary>Storage protocol only. No Kubernetes models, resource types, authorization or event dependencies.</summary>
public sealed class MongoObservationStore<TContext, TControl, TDemand, TSnapshot>(
    IMongoCollection<TControl> controls,
    IMongoCollection<TDemand> demands,
    IMongoCollection<TSnapshot> snapshots,
    MongoRunTransaction transactions,
    IMongoObservationDefinition<TContext, TControl, TDemand, TSnapshot> definition)
    where TControl : class, IObservationControl
    where TDemand : class, IObservationDemand
    where TSnapshot : class, IObservationSnapshot
{
    private readonly MongoObservationFilters<TContext, TControl, TDemand, TSnapshot> filters = new(definition);
    private readonly MongoObservationPipelines<TContext, TControl, TDemand, TSnapshot> pipelines = new(definition);
    private readonly Func<TControl, string?> sourceOf = definition.Source.Compile();

    public Task<MongoObservationOutcome<TControl>> PutDemandAsync(MongoObservationDemand demand,
        TContext context,
        CancellationToken ct) =>
        RunAsync<TControl>(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                var scope = definition.Scope(context);
                var control = await GetOrCreateAsync(context, scope, tx.Session, token);
                await TouchAsync(scope, tx.Session, token);
                var previous = await demands.Find(tx.Session, filters.DemandKey(demand.Identity))
                    .FirstOrDefaultAsync(token);
                if (previous is not null && (previous.ConnectionId != demand.Identity.ConnectionId ||
                                             previous.SessionId != demand.Identity.SessionId))
                    return tx.Abort(Rejected(MongoObservationRejection.Superseded));
                await demands.UpdateOneAsync(tx.Session,
                    filters.DemandKey(demand.Identity),
                    pipelines.RenewDemand(demand),
                    new UpdateOptions { IsUpsert = true },
                    token);
                if (!await demands.Find(tx.Session,
                        filters.LiveDemandKey(demand.Identity)).AnyAsync(token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var changed = !definition.HasPin(control, context);
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    filters.ControlScope(scope),
                    pipelines.ActivateDemand(context, changed),
                    AfterControl(),
                    token);
                if (changed)
                    await definition.PublishAsync(updated, tx.Session, token);
                await snapshots.UpdateOneAsync(tx.Session,
                    filters.SnapshotScope(scope),
                    pipelines.ClearSnapshotExpiry(),
                    cancellationToken: token);
                // Check the exact interest again after the final awaited storage/outbox operations.
                if (!await demands.Find(tx.Session,
                        filters.LiveDemandKey(demand.Identity)).AnyAsync(token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                await TouchAsync(scope, tx.Session, token);
                return tx.Commit(Success(updated));
            },
            ct);

    public Task<MongoObservationOutcome<MongoObservationAccepted>>
        RemoveDemandAsync(MongoObservationDemandIdentity demand, CancellationToken ct) =>
        RunAsync<MongoObservationAccepted>(async (tx, token) =>
            {
                var control = await controls.Find(tx.Session, filters.ControlScope(demand.Scope))
                    .FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Commit(Success(new MongoObservationAccepted()));
                await TouchAsync(demand.Scope, tx.Session, token);
                await demands.DeleteOneAsync(tx.Session,
                    filters.ExactDemand(demand),
                    cancellationToken: token);
                await MarkIdleAsync(demand.Scope, tx.Session, token);
                return tx.Commit(Success(new MongoObservationAccepted()));
            },
            ct);

    public async Task<MongoObservationPage> ScanAsync(string? after, int limit, CancellationToken ct)
    {
        var rows = await demands.Aggregate().Match(filters.DemandPage(after))
            .Group(MongoObservationExpressions.DemandSubjects(definition.DemandSubject, definition.DemandTenant))
            .SortBy(item => item.Id).Limit(limit + 1).ToListAsync(ct);
        var selected = rows.Take(limit)
            .Select(row => new MongoObservationScope(row.Id, row.TenantId)).ToArray();
        return new(selected, rows.Count > limit ? selected[^1].Id : null);
    }

    public async Task ReapAsync(int limit, CancellationToken ct)
    {
        var candidates = await controls.Find(filters.ActiveControls())
            .SortBy(item => item.UpdatedAtUtc).Limit(limit).ToListAsync(ct);
        foreach (var candidate in candidates)
            await RunAsync<MongoObservationAccepted>(async (tx, token) =>
                {
                    var scope = definition.ScopeOf(candidate);
                    var control = await controls.Find(tx.Session, filters.ControlScope(scope))
                        .FirstOrDefaultAsync(token);
                    if (control is null || control.IdleSinceUtc is not null)
                        return tx.Commit(Success(new MongoObservationAccepted()));
                    await TouchAsync(scope, tx.Session, token);
                    await demands.DeleteManyAsync(tx.Session,
                        filters.ExpiredDemand(scope),
                        cancellationToken: token);
                    await MarkIdleAsync(scope, tx.Session, token);
                    return tx.Commit(Success(new MongoObservationAccepted()));
                },
                ct);
    }

    public Task<MongoObservationOutcome<TControl>> ClaimAsync(TContext context,
        string instanceId,
        CancellationToken ct) =>
        RunAsync<TControl>(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                var scope = definition.Scope(context);
                if (!await HasDemandAsync(scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var control = await GetOrCreateAsync(context, scope, tx.Session, token);
                if (definition.HasPin(control, context) &&
                    await controls.Find(tx.Session, filters.BusyLease(scope)).AnyAsync(token))
                    return tx.Abort(Rejected(MongoObservationRejection.Busy));
                var changed = control.SourceHealthy || sourceOf(control) is not null ||
                              !definition.HasPin(control, context);
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    filters.Epoch(scope, control.Epoch),
                    pipelines.Claim(context, instanceId, changed),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                if (changed)
                    await definition.PublishAsync(updated, tx.Session, token);
                if (await DeadlineAsync(LeaseOf(updated), context, updated, tx.Session, token) is { } deadline)
                    return tx.Abort(deadline);
                return tx.Commit(Success(updated));
            },
            ct);

    public Task<MongoObservationOutcome<TControl>>
        RenewAsync(MongoObservationLease lease, TContext context, CancellationToken ct) =>
        RunAsync<TControl>(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                if (!await HasDemandAsync(lease.Scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    filters.ActiveLease(lease, context),
                    pipelines.RenewLease(),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                if (await DeadlineAsync(lease, context, updated, tx.Session, token) is { } deadline)
                    return tx.Abort(deadline);
                return tx.Commit(Success(updated));
            },
            ct);

    public Task<MongoObservationOutcome<TControl>> ReleaseAsync(MongoObservationLease lease, CancellationToken ct) =>
        RunAsync<TControl>(async (tx, token) =>
            {
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    filters.LeaseIdentity(lease),
                    pipelines.Release(),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                await definition.PublishAsync(updated, tx.Session, token);
                await MarkIdleAsync(lease.Scope, tx.Session, token);
                return tx.Commit(Success(updated));
            },
            ct);

    public Task<MongoObservationOutcome<TControl>> SetSourceAsync(MongoObservationLease lease,
        TContext context,
        long version,
        string? sourceId,
        CancellationToken ct) =>
        RunAsync<TControl>(async (tx, token) =>
            {
                if (sourceId is not null && await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                if (!await HasDemandAsync(lease.Scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var control = await controls.Find(tx.Session, filters.ActiveLease(lease, context))
                    .FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                if (control.SourceControlVersion != version)
                    return tx.Abort(Rejected(MongoObservationRejection.SourceChanged));
                if (control.SourceHealthy == (sourceId is not null) && sourceOf(control) == sourceId)
                {
                    await TouchAsync(lease.Scope, tx.Session, token);
                    if (sourceId is not null && await DeadlineAsync(lease, context, control, tx.Session, token) is
                        { } quietDeadline)
                        return tx.Abort(quietDeadline);
                    return tx.Commit(Success(control));
                }

                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    filters.SourceVersion(lease, context, version),
                    pipelines.SetSource(sourceId),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                await definition.PublishAsync(updated, tx.Session, token);
                if (sourceId is not null && await DeadlineAsync(lease, context, updated, tx.Session, token) is
                    { } deadline)
                    return tx.Abort(deadline);
                return tx.Commit(Success(updated));
            },
            ct);

    public Task<MongoObservationOutcome<TSnapshot>> CommitSnapshotAsync(MongoObservationLease lease,
        TContext context,
        long sourceVersion,
        string sourceId,
        Func<TControl, TSnapshot> buildSnapshot,
        CancellationToken ct)
    {
        long? expectedVersion = null; // Retain the originally submitted cut across Mongo transaction retries.
        return RunAsync<TSnapshot>(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                if (!await HasDemandAsync(lease.Scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var control = await controls.Find(tx.Session, filters.ActiveLease(lease, context))
                    .FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                if (!control.SourceHealthy || control.SourceControlVersion != sourceVersion ||
                    sourceOf(control) != sourceId)
                    return tx.Abort(Rejected(MongoObservationRejection.SourceChanged));
                expectedVersion ??= control.ObservationVersion;
                if (control.ObservationVersion != expectedVersion)
                    return tx.Abort(Rejected(MongoObservationRejection.Superseded));
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    filters.SnapshotCommit(lease, context, sourceVersion, sourceId, expectedVersion.Value),
                    pipelines.CommitSnapshot(),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                var snapshot = buildSnapshot(updated);
                await snapshots.ReplaceOneAsync(tx.Session,
                    filters.SnapshotScope(lease.Scope),
                    snapshot,
                    new ReplaceOptions { IsUpsert = true },
                    token);
                await definition.PublishAsync(updated, tx.Session, token);
                if (await DeadlineAsync(lease, context, updated, tx.Session, token) is { } deadline)
                    return tx.Abort(deadline);
                return tx.Commit(Success(snapshot));
            },
            ct);
    }

    public async Task<TControl?> LoadControlAsync(MongoObservationScope scope, CancellationToken ct) =>
        await controls.Find(filters.ControlScope(scope)).FirstOrDefaultAsync(ct);

    public async Task<TSnapshot?> LoadFreshAsync(TContext context, CancellationToken ct)
    {
        var result = await RunAsync<TSnapshot>(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                var scope = definition.Scope(context);
                if (!await HasDemandAsync(scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var control = await controls.Find(tx.Session, filters.HealthyLease(context))
                    .FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                var snapshot = await snapshots.Find(tx.Session,
                    filters.FreshSnapshot(context, control, sourceOf(control))).FirstOrDefaultAsync(token);
                if (snapshot is null || !definition.AcceptSnapshot(snapshot))
                    return tx.Abort(Rejected(MongoObservationRejection.Superseded));
                if (await DeadlineAsync(LeaseOf(control), context, control, tx.Session, token) is { } deadline)
                    return tx.Abort(deadline);
                return tx.Commit(Success(snapshot));
            },
            ct);
        return result switch
        {
            MongoObservationSuccess<TSnapshot> success => success.Value,
            MongoObservationRejected => null
        };
    }

    private async Task<TControl> GetOrCreateAsync(TContext context,
        MongoObservationScope scope,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var control = await controls.Find(session, filters.ControlScope(scope)).FirstOrDefaultAsync(ct);
        if (control is not null)
            return control;
        control = definition.CreateControl(context);
        try
        {
            await controls.InsertOneAsync(session, control, cancellationToken: ct);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new ControlCreationRaceException();
        }

        return control;
    }

    private MongoObservationLease LeaseOf(TControl control) => new(definition.ScopeOf(control),
        control.InstanceId!,
        control.Token!,
        control.Epoch);

    private Task TouchAsync(MongoObservationScope scope, IClientSessionHandle session, CancellationToken ct) =>
        controls.UpdateOneAsync(session, filters.ControlScope(scope), pipelines.Touch(), cancellationToken: ct);

    private Task<bool> HasDemandAsync(MongoObservationScope scope,
        IClientSessionHandle session,
        CancellationToken ct) =>
        demands.Find(session, filters.ActiveDemand(scope)).AnyAsync(ct);

    private async Task MarkIdleAsync(MongoObservationScope scope, IClientSessionHandle session, CancellationToken ct)
    {
        if (await HasDemandAsync(scope, session, ct))
            return;
        await controls.UpdateOneAsync(session,
            filters.NotIdle(scope),
            pipelines.MarkIdle(),
            cancellationToken: ct);
        await snapshots.UpdateOneAsync(session,
            filters.SnapshotScope(scope),
            pipelines.RetainIdleSnapshot(),
            cancellationToken: ct);
    }

    private async Task<MongoObservationRejected?> DeadlineAsync(MongoObservationLease lease,
        TContext context,
        TControl expected,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!await HasDemandAsync(lease.Scope, session, ct))
            return Rejected(MongoObservationRejection.NoDemand);
        var result = await controls.UpdateOneAsync(session,
            filters.Deadline(lease, context, expected),
            pipelines.Touch(),
            cancellationToken: ct);
        return result.MatchedCount == 1 ? null : Rejected(MongoObservationRejection.LeaseLost);
    }

    private async Task<MongoObservationOutcome<T>> RunAsync<T>(
        Func<MongoTransactionContext<MongoObservationOutcome<T>>, CancellationToken,
            Task<MongoTransactionDecision<MongoObservationOutcome<T>>>> action,
        CancellationToken ct)
        where T : notnull
    {
        for (var attempt = 0; ; attempt++)
            try
            {
                return await transactions.InvokeOutcomeAsync(action, ct);
            }
            catch (ControlCreationRaceException) when (attempt < 3)
            {
            }
    }

    private sealed class ControlCreationRaceException : Exception;

    private static MongoObservationSuccess<T> Success<T>(T value) where T : notnull => new(value);
    private static MongoObservationRejected Rejected(MongoObservationRejection reason) => new(reason);
    private static FindOneAndUpdateOptions<TControl> AfterControl() => new() { ReturnDocument = ReturnDocument.After };
}
