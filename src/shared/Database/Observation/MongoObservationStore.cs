using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Observation;
using Control = Pole.Database.Observation.IMongoObservationControl;
using Demand = Pole.Database.Observation.IMongoObservationDemand;
using Snapshot = Pole.Database.Observation.IMongoObservationSnapshot;

namespace Pole.Database.Observation;

/// <summary>Storage protocol only. No Kubernetes models, resource types, authorization or event dependencies.</summary>
public sealed class MongoObservationStore<TContext, TControl, TDemand, TSnapshot>(
    IMongoDatabase database,
    MongoRunTransaction transactions,
    string controlsCollection,
    string demandsCollection,
    string snapshotsCollection,
    IMongoObservationDefinition<TContext, TControl, TDemand, TSnapshot> definition)
    where TControl : class, IMongoObservationControl
    where TDemand : class, IMongoObservationDemand
    where TSnapshot : class, IMongoObservationSnapshot
{
    private readonly IMongoCollection<TControl> controls = database.GetCollection<TControl>(controlsCollection);
    private readonly IMongoCollection<TDemand> demands = database.GetCollection<TDemand>(demandsCollection);

    private readonly IMongoCollection<TSnapshot> snapshots =
        database.GetCollection<TSnapshot>(snapshotsCollection);

    private readonly string demandSubjectField = FieldName(definition.DemandSubject);
    private readonly string sourceField = FieldName(definition.Source);
    private readonly string snapshotSourceField = FieldName(definition.SnapshotSource);
    private readonly Func<TControl, string?> sourceOf = definition.Source.Compile();

    public Task<MongoObservationOutcome<TControl>> PutDemandAsync(MongoObservationDemand demand,
        TContext context,
        CancellationToken ct) =>
        RunAsync<TControl>(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                var scope = definition.Scope(context);
                var pin = PinFields(context);
                var control = await GetOrCreateAsync(context, scope, tx.Session, token);
                await TouchAsync(scope, tx.Session, token);
                var key = new BsonDocument("_id", demand.Identity.Scope.DemandKey(demand.Identity.SubscriptionId));
                var previous = await demands.Find(tx.Session, key).FirstOrDefaultAsync(token);
                if (previous is not null && (previous.ConnectionId != demand.Identity.ConnectionId ||
                                             previous.SessionId != demand.Identity.SessionId))
                    return tx.Abort(Rejected(MongoObservationRejection.Superseded));
                var bound = new BsonDocument("$min",
                    new BsonArray
                    {
                        new BsonDocument("$ifNull",
                            new BsonArray { "$OriginalExpiresAtUtc", Literal(demand.ExpiresAtUtc.UtcDateTime) }),
                        Literal(demand.ExpiresAtUtc.UtcDateTime)
                    });
                var fields = new BsonDocument();
                foreach (var element in DemandFilter(scope))
                    fields.Add(element.Name, Literal(element.Value));
                fields.Add(nameof(Demand.SubscriptionId), Literal(demand.Identity.SubscriptionId));
                fields.Add(nameof(Demand.ConnectionId), Literal(demand.Identity.ConnectionId));
                fields.Add(nameof(Demand.SessionId), Literal(demand.Identity.SessionId));
                fields.Add(nameof(Demand.OriginalExpiresAtUtc), bound);
                fields.Add(nameof(Demand.ExpiresAtUtc),
                    new BsonDocument("$min", new BsonArray { bound, After(ObservationPolicy.DemandDuration) }));
                await demands.UpdateOneAsync(tx.Session,
                    key,
                    Pipeline<TDemand>(fields),
                    new UpdateOptions { IsUpsert = true },
                    token);
                if (!await demands.Find(tx.Session,
                        And(key, Expiry(nameof(Demand.ExpiresAtUtc), "$gt"))).AnyAsync(token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var changed = !definition.HasPin(control, context);
                fields = new() { { nameof(Control.IdleSinceUtc), BsonNull.Value } };
                if (changed)
                {
                    Invalidate(fields);
                    fields.Add(nameof(Control.ObservationVersion), Increment(nameof(Control.ObservationVersion)));
                    foreach (var element in pin)
                        fields.Add(element.Name, Literal(element.Value));
                }

                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    ControlFilter(scope),
                    ControlPipeline(fields),
                    AfterControl(),
                    token);
                if (changed)
                    await definition.PublishAsync(updated, tx.Session, token);
                await snapshots.UpdateOneAsync(tx.Session,
                    ControlFilter(scope),
                    Builders<TSnapshot>.Update.Set(item => item.ExpiresAtUtc, null),
                    cancellationToken: token);
                // Check the exact interest again after the final awaited storage/outbox operations.
                if (!await demands.Find(tx.Session,
                        And(key, Expiry(nameof(Demand.ExpiresAtUtc), "$gt"))).AnyAsync(token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                await TouchAsync(scope, tx.Session, token);
                return tx.Commit(Success(updated));
            },
            ct);

    public Task<MongoObservationOutcome<MongoObservationAccepted>>
        RemoveDemandAsync(MongoObservationDemandIdentity demand, CancellationToken ct) =>
        RunAsync<MongoObservationAccepted>(async (tx, token) =>
            {
                var control = await controls.Find(tx.Session, ControlFilter(demand.Scope)).FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Commit(Success(new MongoObservationAccepted()));
                await TouchAsync(demand.Scope, tx.Session, token);
                var identity = new BsonDocument
                {
                    { "_id", demand.Scope.DemandKey(demand.SubscriptionId) },
                    { nameof(Demand.ConnectionId), demand.ConnectionId }, { nameof(Demand.SessionId), demand.SessionId }
                };
                await demands.DeleteOneAsync(tx.Session,
                    And(identity, DemandFilter(demand.Scope)),
                    cancellationToken: token);
                await MarkIdleAsync(demand.Scope, tx.Session, token);
                return tx.Commit(Success(new MongoObservationAccepted()));
            },
            ct);

    public async Task<MongoObservationPage> ScanAsync(string? after, int limit, CancellationToken ct)
    {
        var filter = Expiry(nameof(Demand.ExpiresAtUtc), "$gt");
        if (after is not null)
            filter = And(filter, new(demandSubjectField, new BsonDocument("$gt", ObjectId.Parse(after))));
        var rows = await demands.Aggregate().Match(filter).Group<ObservationDemandGroup>(new BsonDocument
        {
            { "_id", "$" + demandSubjectField }, { "TenantId", new BsonDocument("$first", "$TenantId") }
        }).Sort(new BsonDocument("_id", 1)).Limit(limit + 1).ToListAsync(ct);
        var selected = rows.Take(limit)
            .Select(row => new MongoObservationScope(row.Id.ToString(), row.TenantId?.ToString())).ToArray();
        return new(selected, rows.Count > limit ? selected[^1].Id : null);
    }

    public async Task ReapAsync(int limit, CancellationToken ct)
    {
        var candidates = await controls.Find(Builders<TControl>.Filter.Eq(item => item.IdleSinceUtc, null))
            .SortBy(item => item.UpdatedAtUtc).Limit(limit).ToListAsync(ct);
        foreach (var candidate in candidates)
            await RunAsync<MongoObservationAccepted>(async (tx, token) =>
                {
                    var scope = definition.ScopeOf(candidate);
                    var control = await controls.Find(tx.Session, ControlFilter(scope)).FirstOrDefaultAsync(token);
                    if (control is null || control.IdleSinceUtc is not null)
                        return tx.Commit(Success(new MongoObservationAccepted()));
                    await TouchAsync(scope, tx.Session, token);
                    await demands.DeleteManyAsync(tx.Session,
                        And(DemandFilter(scope), Expiry(nameof(Demand.ExpiresAtUtc), "$lte")),
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
                var pin = PinFields(context);
                if (!await HasDemandAsync(scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var control = await GetOrCreateAsync(context, scope, tx.Session, token);
                if (definition.HasPin(control, context) && await controls.Find(tx.Session,
                        And(ControlFilter(scope),
                            new(nameof(Control.Token), new BsonDocument("$ne", BsonNull.Value)),
                            Expiry(nameof(Control.LeaseExpiresAtUtc), "$gt"))).AnyAsync(token))
                    return tx.Abort(Rejected(MongoObservationRejection.Busy));
                var fields = new BsonDocument
                {
                    { nameof(Control.InstanceId), Literal(instanceId) },
                    { nameof(Control.Token), Literal(Guid.NewGuid().ToString("N")) },
                    { nameof(Control.Epoch), Increment(nameof(Control.Epoch)) },
                    { nameof(Control.SourceControlVersion), Increment(nameof(Control.SourceControlVersion)) },
                    { nameof(Control.LeaseExpiresAtUtc), After(ObservationPolicy.LeaseDuration) },
                    { nameof(Control.SourceHealthy), false },
                    { sourceField, BsonNull.Value }, { nameof(Control.IdleSinceUtc), BsonNull.Value }
                };
                foreach (var element in pin)
                    fields.Add(element.Name, Literal(element.Value));
                var changed = control.SourceHealthy || sourceOf(control) is not null ||
                              !definition.HasPin(control, context);
                if (changed)
                    fields.Add(nameof(Control.ObservationVersion), Increment(nameof(Control.ObservationVersion)));
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    And(ControlFilter(scope), new(nameof(Control.Epoch), control.Epoch)),
                    ControlPipeline(fields),
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
                    And(ActiveLease(lease), PinFields(context)),
                    ControlPipeline(new(nameof(Control.LeaseExpiresAtUtc), After(ObservationPolicy.LeaseDuration))),
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
                var fields = new BsonDocument
                {
                    { nameof(Control.ObservationVersion), Increment(nameof(Control.ObservationVersion)) }
                };
                Invalidate(fields);
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    LeaseIdentity(lease),
                    ControlPipeline(fields),
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
                var control = await controls.Find(tx.Session, And(ActiveLease(lease), PinFields(context)))
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
                    And(ActiveLease(lease), PinFields(context), new(nameof(Control.SourceControlVersion), version)),
                    ControlPipeline(new BsonDocument
                    {
                        { nameof(Control.SourceControlVersion), Increment(nameof(Control.SourceControlVersion)) },
                        { nameof(Control.ObservationVersion), Increment(nameof(Control.ObservationVersion)) },
                        { nameof(Control.SourceHealthy), sourceId is not null },
                        { sourceField, sourceId is null ? BsonNull.Value : Literal(sourceId) }
                    }),
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
                var control = await controls.Find(tx.Session, And(ActiveLease(lease), PinFields(context)))
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
                    And(ActiveLease(lease),
                        PinFields(context),
                        new(nameof(Control.SourceControlVersion), sourceVersion),
                        new(sourceField, sourceId),
                        new(nameof(Control.SourceHealthy), true),
                        new(nameof(Control.ObservationVersion), expectedVersion.Value)),
                    ControlPipeline(new(nameof(Control.ObservationVersion),
                        Increment(nameof(Control.ObservationVersion)))),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                var snapshot = buildSnapshot(updated);
                await snapshots.ReplaceOneAsync(tx.Session,
                    ControlFilter(lease.Scope),
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
        await controls.Find(ControlFilter(scope)).FirstOrDefaultAsync(ct);

    public async Task<TSnapshot?> LoadFreshAsync(TContext context, CancellationToken ct)
    {
        var result = await RunAsync<TSnapshot>(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                var scope = definition.Scope(context);
                if (!await HasDemandAsync(scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var control = await controls.Find(tx.Session,
                    And(ControlFilter(scope),
                        PinFields(context),
                        new(nameof(Control.Token), new BsonDocument("$ne", BsonNull.Value)),
                        new(nameof(Control.SourceHealthy), true),
                        Expiry(nameof(Control.LeaseExpiresAtUtc), "$gt"))).FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                var snapshotCut = And(ControlFilter(scope),
                    new(nameof(Snapshot.SourceControlVersion), control.SourceControlVersion),
                    new(nameof(Snapshot.ObservationVersion), control.ObservationVersion),
                    new(snapshotSourceField, sourceOf(control)),
                    new("$expr", new BsonDocument("$and", new BsonArray
                    {
                        new BsonDocument("$gt",
                            new BsonArray { "$" + nameof(Snapshot.CheckedAtUtc), After(-ObservationPolicy.Freshness) }),
                        new BsonDocument("$lte", new BsonArray { "$" + nameof(Snapshot.CheckedAtUtc), "$$NOW" })
                    })));
                var snapshot = await snapshots.Find(tx.Session,
                    Builders<TSnapshot>.Filter.And(definition.SnapshotPin(context), snapshotCut))
                    .FirstOrDefaultAsync(token);
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
        var control = await controls.Find(session, ControlFilter(scope)).FirstOrDefaultAsync(ct);
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

    // Render only the feature-owned assignment expression. The stored document stays typed.
    private BsonDocument PinFields(TContext context) => definition.PinUpdate(context)
        .Render(new(controls.DocumentSerializer,
            MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry))["$set"].AsBsonDocument;

    private Task TouchAsync(MongoObservationScope scope, IClientSessionHandle session, CancellationToken ct) =>
        controls.UpdateOneAsync(session, ControlFilter(scope), ControlPipeline(new()), cancellationToken: ct);

    private Task<bool> HasDemandAsync(MongoObservationScope scope,
        IClientSessionHandle session,
        CancellationToken ct) =>
        demands.Find(session, And(DemandFilter(scope), Expiry(nameof(Demand.ExpiresAtUtc), "$gt"))).AnyAsync(ct);

    private async Task MarkIdleAsync(MongoObservationScope scope, IClientSessionHandle session, CancellationToken ct)
    {
        if (await HasDemandAsync(scope, session, ct))
            return;
        var fields = new BsonDocument
        {
            { nameof(Control.IdleSinceUtc), "$$NOW" },
            { nameof(Control.ObservationVersion), Increment(nameof(Control.ObservationVersion)) }
        };
        Invalidate(fields);
        await controls.UpdateOneAsync(session,
            And(ControlFilter(scope), new(nameof(Control.IdleSinceUtc), BsonNull.Value)),
            ControlPipeline(fields),
            cancellationToken: ct);
        await snapshots.UpdateOneAsync(session,
            ControlFilter(scope),
            Pipeline<TSnapshot>(new(nameof(Snapshot.ExpiresAtUtc),
                new BsonDocument("$ifNull",
                    new BsonArray { "$" + nameof(Snapshot.ExpiresAtUtc), After(ObservationPolicy.IdleRetention) }))),
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
            And(ActiveLease(lease),
                PinFields(context),
                new(nameof(Control.SourceControlVersion), expected.SourceControlVersion),
                new(nameof(Control.ObservationVersion), expected.ObservationVersion)),
            ControlPipeline(new()),
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

    private sealed record ObservationDemandGroup(
        [property: MongoDB.Bson.Serialization.Attributes.BsonId] ObjectId Id,
        ObjectId? TenantId);

    private static string FieldName<T, TValue>(
        System.Linq.Expressions.Expression<Func<T, TValue>> expression) =>
        new ExpressionFieldDefinition<T, TValue>(expression).Render(
            new(MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry.GetSerializer<T>(),
                MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry)).FieldName;

    private sealed class ControlCreationRaceException : Exception;

    private static MongoObservationSuccess<T> Success<T>(T value) where T : notnull => new(value);
    private static MongoObservationRejected Rejected(MongoObservationRejection reason) => new(reason);
    private static BsonDocument And(params BsonDocument[] filters) => new("$and", new BsonArray(filters));
    private static BsonDocument Literal(BsonValue value) => new("$literal", value);
    private static BsonDocument Literal(DateTime value) => Literal(new BsonDateTime(value));
    private static BsonDocument Increment(string field) => new("$add", new BsonArray { "$" + field, 1L });

    private static BsonDocument After(TimeSpan interval) => new("$dateAdd",
        new BsonDocument { { "startDate", "$$NOW" }, { "unit", "second" }, { "amount", (int)interval.TotalSeconds } });

    private static BsonDocument Expiry(string field, string comparison) => new("$expr",
        new BsonDocument(comparison, new BsonArray { "$" + field, "$$NOW" }));

    private static FindOneAndUpdateOptions<TControl> AfterControl() =>
        new() { ReturnDocument = ReturnDocument.After };

    private static UpdateDefinition<T> Pipeline<T>(BsonDocument fields) =>
        Builders<T>.Update.Pipeline(new[] { new BsonDocument("$set", fields) });

    private static UpdateDefinition<TControl> ControlPipeline(BsonDocument fields)
    {
        fields.Add(nameof(Control.DemandFence), Increment(nameof(Control.DemandFence)));
        fields.Add(nameof(Control.UpdatedAtUtc), "$$NOW");
        return Pipeline<TControl>(fields);
    }

    private static BsonDocument ControlFilter(MongoObservationScope scope) => scope.TenantId is null
        ? new("_id", ObjectId.Parse(scope.Id))
        : new() { { "_id", ObjectId.Parse(scope.Id) }, { "TenantId", ObjectId.Parse(scope.TenantId) } };

    private BsonDocument DemandFilter(MongoObservationScope scope) => scope.TenantId is null
        ? new(demandSubjectField, ObjectId.Parse(scope.Id))
        : new() { { demandSubjectField, ObjectId.Parse(scope.Id) }, { "TenantId", ObjectId.Parse(scope.TenantId) } };

    private BsonDocument LeaseIdentity(MongoObservationLease lease) => And(ControlFilter(lease.Scope),
        new(nameof(Control.InstanceId), lease.InstanceId),
        new(nameof(Control.Token), lease.Token),
        new(nameof(Control.Epoch), lease.Epoch));

    private BsonDocument ActiveLease(MongoObservationLease lease) =>
        And(LeaseIdentity(lease), Expiry(nameof(Control.LeaseExpiresAtUtc), "$gt"));

    private void Invalidate(BsonDocument fields)
    {
        fields.Add(nameof(Control.Epoch), Increment(nameof(Control.Epoch)));
        fields.Add(nameof(Control.SourceControlVersion), Increment(nameof(Control.SourceControlVersion)));
        fields.Add(nameof(Control.InstanceId), BsonNull.Value);
        fields.Add(nameof(Control.Token), BsonNull.Value);
        fields.Add(nameof(Control.LeaseExpiresAtUtc), Literal(DateTime.UnixEpoch));
        fields.Add(nameof(Control.SourceHealthy), false);
        fields.Add(sourceField, BsonNull.Value);
    }
}
