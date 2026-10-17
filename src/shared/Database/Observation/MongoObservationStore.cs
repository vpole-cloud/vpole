using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Observation;

namespace Pole.Database.Observation;

/// <summary>Storage protocol only. No Kubernetes models, resource types, authorization or event dependencies.</summary>
public sealed class MongoObservationStore<TContext>(
    IMongoDatabase database,
    MongoRunTransaction transactions,
    string controlsCollection,
    string demandsCollection,
    string snapshotsCollection,
    string demandSubjectField,
    string sourceField,
    IMongoObservationDefinition<TContext> definition)
{
    private readonly IMongoCollection<BsonDocument> controls = database.GetCollection<BsonDocument>(controlsCollection);
    private readonly IMongoCollection<BsonDocument> demands = database.GetCollection<BsonDocument>(demandsCollection);

    private readonly IMongoCollection<BsonDocument> snapshots =
        database.GetCollection<BsonDocument>(snapshotsCollection);

    public Task<MongoObservationOutcome> PutDemandAsync(MongoObservationDemand demand,
        TContext context,
        CancellationToken ct) =>
        RunAsync(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                var scope = definition.Scope(context);
                var pin = definition.Pin(context);
                var control = await GetOrCreateAsync(scope, pin, tx.Session, token);
                await TouchAsync(scope, tx.Session, token);
                var key = new BsonDocument("_id", demand.Identity.Scope.DemandKey(demand.Identity.SubscriptionId));
                var previous = await demands.Find(tx.Session, key).FirstOrDefaultAsync(token);
                if (previous is not null && (previous["ConnectionId"] != demand.Identity.ConnectionId ||
                                             previous["SessionId"] != demand.Identity.SessionId))
                    return tx.Abort(Rejected(MongoObservationRejection.Superseded));
                var bound = new BsonDocument("$min",
                    new BsonArray
                    {
                        new BsonDocument("$ifNull",
                            new BsonArray { "$OriginalExpiresAtUtc", Literal(demand.ExpiresAtUtc.UtcDateTime) }),
                        Literal(demand.ExpiresAtUtc.UtcDateTime)
                    });
                var fields = new BsonDocument();
                foreach (var element in scope.Demands)
                    fields.Add(element.Name, Literal(element.Value));
                fields.Add("SubscriptionId", Literal(demand.Identity.SubscriptionId));
                fields.Add("ConnectionId", Literal(demand.Identity.ConnectionId));
                fields.Add("SessionId", Literal(demand.Identity.SessionId));
                fields.Add("OriginalExpiresAtUtc", bound);
                fields.Add("ExpiresAtUtc",
                    new BsonDocument("$min", new BsonArray { bound, After(ObservationPolicy.DemandDuration) }));
                await demands.UpdateOneAsync(tx.Session,
                    key,
                    Pipeline(fields),
                    new UpdateOptions { IsUpsert = true },
                    token);
                if (!await demands.Find(tx.Session, And(key, Expiry("ExpiresAtUtc", "$gt"))).AnyAsync(token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var changed = !HasPin(control, pin);
                fields = new() { { "IdleSinceUtc", BsonNull.Value } };
                if (changed)
                {
                    Invalidate(fields);
                    fields.Add("ObservationVersion", Increment("ObservationVersion"));
                    foreach (var element in pin)
                        fields.Add(element.Name, Literal(element.Value));
                }

                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    scope.Control,
                    ControlPipeline(fields),
                    AfterControl(),
                    token);
                if (changed)
                    await definition.PublishAsync(updated, tx.Session, token);
                await snapshots.UpdateOneAsync(tx.Session,
                    scope.Control,
                    Builders<BsonDocument>.Update.Set("ExpiresAtUtc", BsonNull.Value),
                    cancellationToken: token);
                // Check the exact interest again after the final awaited storage/outbox operations.
                if (!await demands.Find(tx.Session, And(key, Expiry("ExpiresAtUtc", "$gt"))).AnyAsync(token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                await TouchAsync(scope, tx.Session, token);
                return tx.Commit(updated);
            },
            ct);

    public Task<MongoObservationOutcome>
        RemoveDemandAsync(MongoObservationDemandIdentity demand, CancellationToken ct) =>
        RunAsync(async (tx, token) =>
            {
                var control = await controls.Find(tx.Session, demand.Scope.Control).FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Commit(new BsonDocument());
                await TouchAsync(demand.Scope, tx.Session, token);
                var identity = new BsonDocument
                {
                    { "_id", demand.Scope.DemandKey(demand.SubscriptionId) },
                    { "ConnectionId", demand.ConnectionId }, { "SessionId", demand.SessionId }
                };
                await demands.DeleteOneAsync(tx.Session, And(identity, demand.Scope.Demands), cancellationToken: token);
                await MarkIdleAsync(demand.Scope, tx.Session, token);
                return tx.Commit(control);
            },
            ct);

    public async Task<MongoObservationPage> ScanAsync(string? after, int limit, CancellationToken ct)
    {
        var filter = Expiry("ExpiresAtUtc", "$gt");
        if (after is not null)
            filter = And(filter, new(demandSubjectField, new BsonDocument("$gt", ObjectId.Parse(after))));
        var rows = await demands.Aggregate().Match(filter).Group(new BsonDocument
        {
            { "_id", "$" + demandSubjectField }, { "TenantId", new BsonDocument("$first", "$TenantId") }
        }).Sort(new BsonDocument("_id", 1)).Limit(limit + 1).ToListAsync(ct);
        var selected = rows.Take(limit).Select(row => new MongoObservationScope(row["_id"].AsObjectId.ToString(),
            row["TenantId"].IsBsonNull ? null : row["TenantId"].AsObjectId.ToString(),
            demandSubjectField)).ToArray();
        return new(selected, rows.Count > limit ? selected[^1].Id : null);
    }

    public async Task ReapAsync(int limit, CancellationToken ct)
    {
        var candidates = await controls.Find(new BsonDocument("IdleSinceUtc", BsonNull.Value))
            .Sort(new BsonDocument("UpdatedAtUtc", 1)).Limit(limit).ToListAsync(ct);
        foreach (var candidate in candidates)
            await RunAsync(async (tx, token) =>
                {
                    var scope = ScopeOf(candidate);
                    var control = await controls.Find(tx.Session, scope.Control).FirstOrDefaultAsync(token);
                    if (control is null || !control["IdleSinceUtc"].IsBsonNull)
                        return tx.Commit(new BsonDocument());
                    await TouchAsync(scope, tx.Session, token);
                    await demands.DeleteManyAsync(tx.Session,
                        And(scope.Demands, Expiry("ExpiresAtUtc", "$lte")),
                        cancellationToken: token);
                    await MarkIdleAsync(scope, tx.Session, token);
                    return tx.Commit(control);
                },
                ct);
    }

    public Task<MongoObservationOutcome> ClaimAsync(TContext context, string instanceId, CancellationToken ct) =>
        RunAsync(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                var scope = definition.Scope(context);
                var pin = definition.Pin(context);
                if (!await HasDemandAsync(scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var control = await GetOrCreateAsync(scope, pin, tx.Session, token);
                if (HasPin(control, pin) && await controls.Find(tx.Session,
                        And(scope.Control,
                            new("Token", new BsonDocument("$ne", BsonNull.Value)),
                            Expiry("LeaseExpiresAtUtc", "$gt"))).AnyAsync(token))
                    return tx.Abort(Rejected(MongoObservationRejection.Busy));
                var fields = new BsonDocument
                {
                    { "InstanceId", Literal(instanceId) }, { "Token", Literal(Guid.NewGuid().ToString("N")) },
                    { "Epoch", Increment("Epoch") }, { "SourceControlVersion", Increment("SourceControlVersion") },
                    { "LeaseExpiresAtUtc", After(ObservationPolicy.LeaseDuration) }, { "SourceHealthy", false },
                    { sourceField, BsonNull.Value }, { "IdleSinceUtc", BsonNull.Value }
                };
                foreach (var element in pin)
                    fields.Add(element.Name, Literal(element.Value));
                var changed = control["SourceHealthy"].AsBoolean || !control[sourceField].IsBsonNull ||
                              !HasPin(control, pin);
                if (changed)
                    fields.Add("ObservationVersion", Increment("ObservationVersion"));
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    And(scope.Control, new("Epoch", control["Epoch"])),
                    ControlPipeline(fields),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                if (changed)
                    await definition.PublishAsync(updated, tx.Session, token);
                if (await DeadlineAsync(LeaseOf(updated), context, updated, tx.Session, token) is { } deadline)
                    return tx.Abort(deadline);
                return tx.Commit(updated);
            },
            ct);

    public Task<MongoObservationOutcome>
        RenewAsync(MongoObservationLease lease, TContext context, CancellationToken ct) =>
        RunAsync(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                if (!await HasDemandAsync(lease.Scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    And(ActiveLease(lease), definition.Pin(context)),
                    ControlPipeline(new("LeaseExpiresAtUtc", After(ObservationPolicy.LeaseDuration))),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                if (await DeadlineAsync(lease, context, updated, tx.Session, token) is { } deadline)
                    return tx.Abort(deadline);
                return tx.Commit(updated);
            },
            ct);

    public Task<MongoObservationOutcome> ReleaseAsync(MongoObservationLease lease, CancellationToken ct) =>
        RunAsync(async (tx, token) =>
            {
                var fields = new BsonDocument { { "ObservationVersion", Increment("ObservationVersion") } };
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
                return tx.Commit(updated);
            },
            ct);

    public Task<MongoObservationOutcome> SetSourceAsync(MongoObservationLease lease,
        TContext context,
        long version,
        string? sourceId,
        CancellationToken ct) =>
        RunAsync(async (tx, token) =>
            {
                if (sourceId is not null && await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                if (!await HasDemandAsync(lease.Scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var control = await controls.Find(tx.Session, And(ActiveLease(lease), definition.Pin(context)))
                    .FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                if (control["SourceControlVersion"].ToInt64() != version)
                    return tx.Abort(Rejected(MongoObservationRejection.SourceChanged));
                if (control["SourceHealthy"].AsBoolean == (sourceId is not null) && control[sourceField] ==
                    (sourceId is null ? BsonNull.Value : new BsonString(sourceId)))
                {
                    await TouchAsync(lease.Scope, tx.Session, token);
                    if (sourceId is not null && await DeadlineAsync(lease, context, control, tx.Session, token) is
                        { } quietDeadline)
                        return tx.Abort(quietDeadline);
                    return tx.Commit(control);
                }

                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    And(ActiveLease(lease), definition.Pin(context), new("SourceControlVersion", version)),
                    ControlPipeline(new BsonDocument
                    {
                        { "SourceControlVersion", Increment("SourceControlVersion") },
                        { "ObservationVersion", Increment("ObservationVersion") },
                        { "SourceHealthy", sourceId is not null },
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
                return tx.Commit(updated);
            },
            ct);

    public Task<MongoObservationOutcome> CommitSnapshotAsync(MongoObservationLease lease,
        TContext context,
        long sourceVersion,
        string sourceId,
        Func<BsonDocument, BsonDocument> buildSnapshot,
        CancellationToken ct)
    {
        long? expectedVersion = null; // Retain the originally submitted cut across Mongo transaction retries.
        return RunAsync(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                if (!await HasDemandAsync(lease.Scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var control = await controls.Find(tx.Session, And(ActiveLease(lease), definition.Pin(context)))
                    .FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                if (!control["SourceHealthy"].AsBoolean || control["SourceControlVersion"].ToInt64() != sourceVersion ||
                    control[sourceField] != sourceId)
                    return tx.Abort(Rejected(MongoObservationRejection.SourceChanged));
                expectedVersion ??= control["ObservationVersion"].ToInt64();
                if (control["ObservationVersion"].ToInt64() != expectedVersion)
                    return tx.Abort(Rejected(MongoObservationRejection.Superseded));
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    And(ActiveLease(lease),
                        definition.Pin(context),
                        new("SourceControlVersion", sourceVersion),
                        new(sourceField, sourceId),
                        new("SourceHealthy", true),
                        new("ObservationVersion", expectedVersion.Value)),
                    ControlPipeline(new("ObservationVersion", Increment("ObservationVersion"))),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                var snapshot = buildSnapshot(updated);
                await snapshots.ReplaceOneAsync(tx.Session,
                    lease.Scope.Control,
                    snapshot,
                    new ReplaceOptions { IsUpsert = true },
                    token);
                await definition.PublishAsync(updated, tx.Session, token);
                if (await DeadlineAsync(lease, context, updated, tx.Session, token) is { } deadline)
                    return tx.Abort(deadline);
                return tx.Commit(snapshot);
            },
            ct);
    }

    public async Task<BsonDocument?> LoadControlAsync(MongoObservationScope scope, CancellationToken ct) =>
        await controls.Find(scope.Control).FirstOrDefaultAsync(ct);

    public async Task<BsonDocument?> LoadFreshAsync(TContext context, CancellationToken ct)
    {
        var result = await RunAsync(async (tx, token) =>
            {
                if (await definition.FenceAsync(context, tx.Session, token) is { } rejection)
                    return tx.Abort(rejection);
                var scope = definition.Scope(context);
                if (!await HasDemandAsync(scope, tx.Session, token))
                    return tx.Abort(Rejected(MongoObservationRejection.NoDemand));
                var control = await controls.Find(tx.Session,
                    And(scope.Control,
                        definition.Pin(context),
                        new("Token", new BsonDocument("$ne", BsonNull.Value)),
                        new("SourceHealthy", true),
                        Expiry("LeaseExpiresAtUtc", "$gt"))).FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Abort(Rejected(MongoObservationRejection.LeaseLost));
                var snapshot = await snapshots.Find(tx.Session,
                    And(scope.Control,
                        definition.Pin(context),
                        new("SourceControlVersion", control["SourceControlVersion"]),
                        new("ObservationVersion", control["ObservationVersion"]),
                        new(sourceField, control[sourceField]),
                        new("$expr",
                            new BsonDocument("$and",
                                new BsonArray
                                {
                                    new BsonDocument("$gt",
                                        new BsonArray { "$CheckedAtUtc", After(-ObservationPolicy.Freshness) }),
                                    new BsonDocument("$lte", new BsonArray { "$CheckedAtUtc", "$$NOW" })
                                })))).FirstOrDefaultAsync(token);
                if (snapshot is null || !definition.AcceptSnapshot(snapshot))
                    return tx.Abort(Rejected(MongoObservationRejection.Superseded));
                if (await DeadlineAsync(LeaseOf(control), context, control, tx.Session, token) is { } deadline)
                    return tx.Abort(deadline);
                return tx.Commit(snapshot);
            },
            ct);
        return result.Value as BsonDocument;
    }

    private async Task<BsonDocument> GetOrCreateAsync(MongoObservationScope scope,
        BsonDocument pin,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var control = await controls.Find(session, scope.Control).FirstOrDefaultAsync(ct);
        if (control is not null)
            return control;
        control = scope.Control;
        control.AddRange(pin);
        control.AddRange(new BsonDocument
        {
            { "Epoch", 0L }, { "SourceControlVersion", 0L }, { "ObservationVersion", 0L }, { "DemandFence", 0L },
            { "InstanceId", BsonNull.Value }, { "Token", BsonNull.Value }, { "LeaseExpiresAtUtc", DateTime.UnixEpoch },
            { "SourceHealthy", false }, { sourceField, BsonNull.Value }, { "IdleSinceUtc", BsonNull.Value },
            { "UpdatedAtUtc", DateTime.UnixEpoch }
        });
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

    private MongoObservationScope ScopeOf(BsonDocument control) => new(control["_id"].AsObjectId.ToString(),
        control.TryGetValue("TenantId", out var tenant) ? tenant.AsObjectId.ToString() : null,
        demandSubjectField);

    private MongoObservationLease LeaseOf(BsonDocument control) => new(ScopeOf(control),
        control["InstanceId"].AsString,
        control["Token"].AsString,
        control["Epoch"].ToInt64());

    private static bool HasPin(BsonDocument control, BsonDocument pin) => pin.All(element =>
        control.TryGetValue(element.Name, out var value) && value == element.Value);

    private Task TouchAsync(MongoObservationScope scope, IClientSessionHandle session, CancellationToken ct) =>
        controls.UpdateOneAsync(session, scope.Control, ControlPipeline(new()), cancellationToken: ct);

    private Task<bool> HasDemandAsync(MongoObservationScope scope,
        IClientSessionHandle session,
        CancellationToken ct) =>
        demands.Find(session, And(scope.Demands, Expiry("ExpiresAtUtc", "$gt"))).AnyAsync(ct);

    private async Task MarkIdleAsync(MongoObservationScope scope, IClientSessionHandle session, CancellationToken ct)
    {
        if (await HasDemandAsync(scope, session, ct))
            return;
        var fields = new BsonDocument
            { { "IdleSinceUtc", "$$NOW" }, { "ObservationVersion", Increment("ObservationVersion") } };
        Invalidate(fields);
        await controls.UpdateOneAsync(session,
            And(scope.Control, new("IdleSinceUtc", BsonNull.Value)),
            ControlPipeline(fields),
            cancellationToken: ct);
        await snapshots.UpdateOneAsync(session,
            scope.Control,
            Pipeline(new("ExpiresAtUtc",
                new BsonDocument("$ifNull",
                    new BsonArray { "$ExpiresAtUtc", After(ObservationPolicy.IdleRetention) }))),
            cancellationToken: ct);
    }

    private async Task<MongoObservationRejected?> DeadlineAsync(MongoObservationLease lease,
        TContext context,
        BsonDocument expected,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!await HasDemandAsync(lease.Scope, session, ct))
            return Rejected(MongoObservationRejection.NoDemand);
        var result = await controls.UpdateOneAsync(session,
            And(ActiveLease(lease),
                definition.Pin(context),
                new("SourceControlVersion", expected["SourceControlVersion"]),
                new("ObservationVersion", expected["ObservationVersion"])),
            ControlPipeline(new()),
            cancellationToken: ct);
        return result.MatchedCount == 1 ? null : Rejected(MongoObservationRejection.LeaseLost);
    }

    private async Task<MongoObservationOutcome> RunAsync(
        Func<MongoTransactionContext<MongoObservationOutcome>, CancellationToken,
            Task<MongoTransactionDecision<MongoObservationOutcome>>> action,
        CancellationToken ct)
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

    private static MongoObservationRejected Rejected(MongoObservationRejection reason) => new(reason);
    private static BsonDocument And(params BsonDocument[] filters) => new("$and", new BsonArray(filters));
    private static BsonDocument Literal(BsonValue value) => new("$literal", value);
    private static BsonDocument Literal(DateTime value) => Literal(new BsonDateTime(value));
    private static BsonDocument Increment(string field) => new("$add", new BsonArray { "$" + field, 1L });

    private static BsonDocument After(TimeSpan interval) => new("$dateAdd",
        new BsonDocument { { "startDate", "$$NOW" }, { "unit", "second" }, { "amount", (int)interval.TotalSeconds } });

    private static BsonDocument Expiry(string field, string comparison) => new("$expr",
        new BsonDocument(comparison, new BsonArray { "$" + field, "$$NOW" }));

    private static FindOneAndUpdateOptions<BsonDocument> AfterControl() =>
        new() { ReturnDocument = ReturnDocument.After };

    private static UpdateDefinition<BsonDocument> Pipeline(BsonDocument fields) =>
        Builders<BsonDocument>.Update.Pipeline(new[] { new BsonDocument("$set", fields) });

    private static UpdateDefinition<BsonDocument> ControlPipeline(BsonDocument fields)
    {
        fields.Add("DemandFence", Increment("DemandFence"));
        fields.Add("UpdatedAtUtc", "$$NOW");
        return Pipeline(fields);
    }

    private BsonDocument LeaseIdentity(MongoObservationLease lease) => And(lease.Scope.Control,
        new("InstanceId", lease.InstanceId),
        new("Token", lease.Token),
        new("Epoch", lease.Epoch));

    private BsonDocument ActiveLease(MongoObservationLease lease) =>
        And(LeaseIdentity(lease), Expiry("LeaseExpiresAtUtc", "$gt"));

    private void Invalidate(BsonDocument fields)
    {
        fields.Add("Epoch", Increment("Epoch"));
        fields.Add("SourceControlVersion", Increment("SourceControlVersion"));
        fields.Add("InstanceId", BsonNull.Value);
        fields.Add("Token", BsonNull.Value);
        fields.Add("LeaseExpiresAtUtc", Literal(DateTime.UnixEpoch));
        fields.Add("SourceHealthy", false);
        fields.Add(sourceField, BsonNull.Value);
    }
}
