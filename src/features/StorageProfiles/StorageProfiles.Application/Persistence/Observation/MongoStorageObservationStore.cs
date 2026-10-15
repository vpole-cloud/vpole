using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Events;
using static Pole.Features.StorageProfiles.Application.Persistence.Observation.StorageObservationFilters;

namespace Pole.Features.StorageProfiles.Application.Persistence.Observation;

internal sealed class MongoStorageObservationStore(
    IMongoDatabase database,
    MongoRunTransaction transactions,
    FenceDeploymentTargetObservation fenceTarget,
    StoreOutboxWrites storeOutboxWrites,
    StorageObservationOptions options) : IStorageObservationStore
{
    internal const string ControlsCollection = "StorageObservationControls";
    internal const string DemandsCollection = "StorageObservationDemands";
    internal const string SnapshotsCollection = "StorageObservationSnapshots";

    private readonly IMongoCollection<StorageObservationControlDocument> controls =
        database.GetCollection<StorageObservationControlDocument>(ControlsCollection);

    private readonly IMongoCollection<StorageObservationDemandDocument> demands =
        database.GetCollection<StorageObservationDemandDocument>(DemandsCollection);

    private readonly IMongoCollection<StorageObservationSnapshotDocument> snapshots =
        database.GetCollection<StorageObservationSnapshotDocument>(SnapshotsCollection);

    public Task<StorageObservationMutationOutcome> PutDemandAsync(StorageObservationDemand demand,
        StorageObservationContext context,
        CancellationToken ct = default) => PutOrRenewAsync(demand, context, ct);

    public Task<StorageObservationMutationOutcome> RenewDemandAsync(StorageObservationDemand demand,
        StorageObservationContext context,
        CancellationToken ct = default) => PutOrRenewAsync(demand, context, ct);

    private Task<StorageObservationMutationOutcome> PutOrRenewAsync(StorageObservationDemand demand,
        StorageObservationContext context,
        CancellationToken ct)
    {
        Validate(context);
        Validate(demand.Identity);
        if (context.DeploymentTargetId != demand.Identity.DeploymentTargetId)
            throw new ArgumentException("Demand and context must have the same target.");
        return RunAsync<StorageObservationMutationOutcome>(async (tx, token) =>
            {
                if (!await FenceAsync(context, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.TargetUnavailable));
                var control = await GetOrCreateAsync(context, tx.Session, token);
                await TouchAsync(control, tx.Session, token);
                var key = DemandKey(demand.Identity);
                var previous = await demands.Find(tx.Session, DemandByKey(key)).FirstOrDefaultAsync(token);
                if (previous is not null && (previous.ConnectionId != demand.Identity.ConnectionId ||
                                             previous.SessionId != demand.Identity.SessionId))
                    return tx.Abort(Rejected(StorageObservationRejection.Superseded));
                var bound = new BsonDocument("$min",
                    new BsonArray
                    {
                        new BsonDocument("$ifNull",
                            new BsonArray { "$OriginalExpiresAtUtc", Literal(demand.ExpiresAtUtc.UtcDateTime) }),
                        Literal(demand.ExpiresAtUtc.UtcDateTime)
                    });
                await demands.UpdateOneAsync(tx.Session,
                    DemandByKey(key),
                    DemandPipeline(new BsonDocument
                    {
                        { "DeploymentTargetId", Literal(new BsonObjectId(ObjectId.Parse(context.DeploymentTargetId))) },
                        { "SubscriptionId", Literal(demand.Identity.SubscriptionId) },
                        { "ConnectionId", Literal(demand.Identity.ConnectionId) },
                        { "SessionId", Literal(demand.Identity.SessionId) },
                        { "OriginalExpiresAtUtc", bound },
                        { "ExpiresAtUtc", new BsonDocument("$min", new BsonArray { bound, ServerAfter(60) }) }
                    }),
                    new UpdateOptions { IsUpsert = true },
                    token);
                if (!await demands.Find(tx.Session, DemandByKey(key) & DemandExpiry("$gt")).AnyAsync(token))
                    return tx.Abort(Rejected(StorageObservationRejection.NoDemand));
                var fields = new BsonDocument { { "IdleSinceUtc", BsonNull.Value } };
                if (control.ConnectionVersion != context.ConnectionVersion)
                {
                    Invalidate(fields);
                    fields.Add("ConnectionVersion", context.ConnectionVersion);
                    fields.Add("ObservationVersion", Increment("ObservationVersion"));
                }

                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    Control(control.Id),
                    ControlPipeline(fields),
                    AfterControl(),
                    token);
                if (control.ConnectionVersion != context.ConnectionVersion)
                    await PublishAsync(updated, tx.Session, token);
                await snapshots.UpdateOneAsync(tx.Session,
                    Snapshot(control.Id),
                    Builders<StorageObservationSnapshotDocument>.Update.Set(item => item.ExpiresAtUtc, null),
                    cancellationToken: token);
                // The exact new interest must still be live after all awaited storage/outbox operations.
                if (!await demands.Find(tx.Session, DemandByKey(key) & DemandExpiry("$gt")).AnyAsync(token))
                    return tx.Abort(Rejected(StorageObservationRejection.NoDemand));
                await TouchAsync(updated, tx.Session, token);
                return tx.Commit(new StorageObservationMutationAccepted());
            },
            ct);
    }

    public Task<StorageObservationMutationOutcome> RemoveDemandAsync(StorageObservationDemandIdentity identity,
        CancellationToken ct = default)
    {
        Validate(identity);
        return RunAsync<StorageObservationMutationOutcome>(async (tx, token) =>
            {
                var control = await controls.Find(tx.Session, Control(identity.DeploymentTargetId))
                    .FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Commit(new StorageObservationMutationAccepted());
                await TouchAsync(control, tx.Session, token);
                await demands.DeleteOneAsync(tx.Session, DemandIdentity(identity), cancellationToken: token);
                await MarkIdleAsync(control, tx.Session, token);
                return tx.Commit(new StorageObservationMutationAccepted());
            },
            ct);
    }

    public async Task<StorageObservationDemandPage> ScanDemandsAsync(string? after,
        int limit,
        CancellationToken ct = default)
    {
        ValidateLimit(limit);
        if (after is not null)
            ValidateId(after);
        var rows = await demands.Aggregate().Match(DemandsAfter(after))
            .Group(new BsonDocument("_id", "$DeploymentTargetId")).Sort(new BsonDocument("_id", 1)).Limit(limit + 1)
            .ToListAsync(ct);
        var targets = rows.Take(limit).Select(row => row["_id"].AsObjectId.ToString()).ToArray();
        return new(targets, rows.Count > limit ? targets[^1] : null);
    }

    public async Task ReapExpiredDemandsAsync(int limit, CancellationToken ct = default)
    {
        ValidateLimit(limit);
        var candidates = await controls.Find(NonIdle()).SortBy(item => item.UpdatedAtUtc).Limit(limit).ToListAsync(ct);
        foreach (var candidate in candidates)
            await RunAsync<StorageObservationMutationOutcome>(async (tx, token) =>
                {
                    var control = await controls.Find(tx.Session, Control(candidate.Id)).FirstOrDefaultAsync(token);
                    if (control is null || control.IdleSinceUtc is not null)
                        return tx.Commit(new StorageObservationMutationAccepted());
                    await TouchAsync(control, tx.Session, token);
                    await demands.DeleteManyAsync(tx.Session,
                        DemandScope(control.Id) & DemandExpiry("$lte"),
                        cancellationToken: token);
                    await MarkIdleAsync(control, tx.Session, token);
                    return tx.Commit(new StorageObservationMutationAccepted());
                },
                ct);
    }

    public Task<StorageObservationLeaseOutcome> ClaimLeaseAsync(StorageObservationContext context,
        string instanceId,
        CancellationToken ct = default)
    {
        Validate(context);
        ValidateOpaque(instanceId);
        return RunAsync<StorageObservationLeaseOutcome>(async (tx, token) =>
            {
                if (!await FenceAsync(context, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.TargetUnavailable));
                if (!await HasDemandAsync(context.DeploymentTargetId, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.NoDemand));
                var control = await GetOrCreateAsync(context, tx.Session, token);
                if (control.ConnectionVersion == context.ConnectionVersion &&
                    await controls.Find(tx.Session, ActiveOwner(control.Id)).AnyAsync(token))
                    return tx.Abort(Rejected(StorageObservationRejection.Busy));
                var leaseToken = Guid.NewGuid().ToString("N");
                var fields = new BsonDocument
                {
                    { "ConnectionVersion", context.ConnectionVersion }, { "InstanceId", Literal(instanceId) },
                    { "Token", Literal(leaseToken) },
                    { "Epoch", Increment("Epoch") }, { "SourceControlVersion", Increment("SourceControlVersion") },
                    { "LeaseExpiresAtUtc", ServerAfter(30) },
                    { "SourceHealthy", false }, { "SourceId", BsonNull.Value }, { "IdleSinceUtc", BsonNull.Value }
                };
                var changed = control.SourceHealthy || control.SourceId is not null ||
                              control.ConnectionVersion != context.ConnectionVersion;
                if (changed)
                    fields.Add("ObservationVersion", Increment("ObservationVersion"));
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    AtEpoch(control),
                    ControlPipeline(fields),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(StorageObservationRejection.LeaseLost));
                if (changed)
                    await PublishAsync(updated, tx.Session, token);
                var lease = new StorageObservationLease(updated.Id, instanceId, leaseToken, updated.Epoch);
                if (await DeadlineAsync(lease, context, updated, tx.Session, token) is { } rejected)
                    return tx.Abort(rejected);
                return tx.Commit(new StorageObservationOwnership(lease, updated.SourceControlVersion));
            },
            ct);
    }

    public Task<StorageObservationMutationOutcome> RenewLeaseAsync(StorageObservationLease lease,
        StorageObservationContext context,
        CancellationToken ct = default)
    {
        Validate(lease, context);
        return RunAsync<StorageObservationMutationOutcome>(async (tx, token) =>
            {
                if (!await FenceAsync(context, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.TargetUnavailable));
                if (!await HasDemandAsync(lease.DeploymentTargetId, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.NoDemand));
                var control = await controls.FindOneAndUpdateAsync(tx.Session,
                    ActiveLease(lease) & CurrentContext(context),
                    ControlPipeline(new BsonDocument("LeaseExpiresAtUtc", ServerAfter(30))),
                    AfterControl(),
                    token);
                if (control is null)
                    return tx.Abort(Rejected(StorageObservationRejection.LeaseLost));
                if (await DeadlineAsync(lease, context, control, tx.Session, token) is { } rejected)
                    return tx.Abort(rejected);
                return tx.Commit(new StorageObservationMutationAccepted());
            },
            ct);
    }

    public Task<StorageObservationMutationOutcome> ReleaseLeaseAsync(StorageObservationLease lease,
        CancellationToken ct = default)
    {
        Validate(lease);
        return RunAsync<StorageObservationMutationOutcome>(async (tx, token) =>
            {
                var fields = new BsonDocument();
                Invalidate(fields);
                fields.Add("ObservationVersion", Increment("ObservationVersion"));
                var control = await controls.FindOneAndUpdateAsync(tx.Session,
                    LeaseIdentity(lease),
                    ControlPipeline(fields),
                    AfterControl(),
                    token);
                if (control is null)
                    return tx.Abort(Rejected(StorageObservationRejection.LeaseLost));
                await PublishAsync(control, tx.Session, token);
                await MarkIdleAsync(control, tx.Session, token);
                return tx.Commit(new StorageObservationMutationAccepted());
            },
            ct);
    }

    public Task<StorageObservationSourceOutcome> SetSourceStateAsync(StorageObservationLease lease,
        StorageObservationContext context,
        long expectedSourceVersion,
        bool healthy,
        string? sourceId,
        CancellationToken ct = default)
    {
        Validate(lease, context);
        if (healthy && sourceId is null)
            throw new ArgumentException("Healthy storage coverage requires its source identity.");
        if (sourceId is not null)
            ValidateOpaque(sourceId);
        return RunAsync<StorageObservationSourceOutcome>(async (tx, token) =>
            {
                if (healthy && !await FenceAsync(context, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.TargetUnavailable));
                if (!await HasDemandAsync(lease.DeploymentTargetId, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.NoDemand));
                var current = await controls.Find(tx.Session, ActiveLease(lease) & CurrentContext(context))
                    .FirstOrDefaultAsync(token);
                if (current is null)
                    return tx.Abort(Rejected(StorageObservationRejection.LeaseLost));
                if (current.SourceControlVersion != expectedSourceVersion)
                    return tx.Abort(Rejected(StorageObservationRejection.SourceChanged));
                if (current.SourceHealthy == healthy && current.SourceId == sourceId)
                {
                    await TouchAsync(current, tx.Session, token);
                    if (healthy && await DeadlineAsync(lease, context, current, tx.Session, token) is { } quietRejected)
                        return tx.Abort(quietRejected);
                    return tx.Commit(new StorageObservationSourceState(current.SourceControlVersion));
                }

                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    SourceVersion(lease, context, expectedSourceVersion),
                    ControlPipeline(new BsonDocument
                    {
                        { "SourceControlVersion", Increment("SourceControlVersion") },
                        { "ObservationVersion", Increment("ObservationVersion") }, { "SourceHealthy", healthy },
                        { "SourceId", sourceId is null ? BsonNull.Value : Literal(sourceId) }
                    }),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(StorageObservationRejection.LeaseLost));
                await PublishAsync(updated, tx.Session, token);
                if (healthy && await DeadlineAsync(lease, context, updated, tx.Session, token) is { } rejected)
                    return tx.Abort(rejected);
                return tx.Commit(new StorageObservationSourceState(updated.SourceControlVersion));
            },
            ct);
    }

    public Task<StorageObservationSnapshotOutcome> TryCommitSnapshotAsync(StorageObservationLease lease,
        StorageObservationContext context,
        long sourceVersion,
        string sourceId,
        StorageInventoryObservation inventory,
        CancellationToken ct = default)
    {
        Validate(lease, context);
        ValidateOpaque(sourceId);
        var safe = StorageObservationValueMappings.Sanitize(inventory, options);
        if (safe is null)
            return Task.FromResult<StorageObservationSnapshotOutcome>(Rejected(StorageObservationRejection
                .InventoryIncomplete));
        long? expectedVersion = null;
        return RunAsync<StorageObservationSnapshotOutcome>(async (tx, token) =>
            {
                if (!await FenceAsync(context, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.TargetUnavailable));
                if (!await HasDemandAsync(lease.DeploymentTargetId, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.NoDemand));
                var control = await controls.Find(tx.Session, ActiveLease(lease) & CurrentContext(context))
                    .FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Abort(Rejected(StorageObservationRejection.LeaseLost));
                if (!control.SourceHealthy || control.SourceControlVersion != sourceVersion ||
                    control.SourceId != sourceId)
                    return tx.Abort(Rejected(StorageObservationRejection.SourceChanged));
                expectedVersion ??= control.ObservationVersion;
                if (control.ObservationVersion != expectedVersion)
                    return tx.Abort(Rejected(StorageObservationRejection.Superseded));
                var updated = await controls.FindOneAndUpdateAsync(tx.Session,
                    Commit(lease, context, sourceVersion, sourceId, expectedVersion.Value),
                    ControlPipeline(new BsonDocument("ObservationVersion", Increment("ObservationVersion"))),
                    AfterControl(),
                    token);
                if (updated is null)
                    return tx.Abort(Rejected(StorageObservationRejection.LeaseLost));
                var snapshot = new StorageObservationSnapshotDocument(control.Id,
                    context.ConnectionVersion,
                    sourceId,
                    sourceVersion,
                    updated.ObservationVersion,
                    updated.UpdatedAtUtc,
                    safe,
                    null);
                await snapshots.ReplaceOneAsync(tx.Session,
                    Snapshot(control.Id),
                    snapshot,
                    new ReplaceOptions { IsUpsert = true },
                    token);
                await PublishAsync(updated, tx.Session, token);
                if (await DeadlineAsync(lease, context, updated, tx.Session, token) is { } rejected)
                    return tx.Abort(rejected);
                return tx.Commit(StorageObservationValueMappings.Load(snapshot));
            },
            ct);
    }

    public async Task<StorageObservationControlState?> LoadControlAsync(string targetId, CancellationToken ct = default)
    {
        ValidateId(targetId);
        var control = await controls.Find(Control(targetId)).FirstOrDefaultAsync(ct);
        return control is null
            ? null
            : new(control.ConnectionVersion,
                control.Epoch,
                control.SourceControlVersion,
                control.ObservationVersion,
                control.SourceHealthy,
                control.SourceId,
                control.Token is null ? null : control.LeaseExpiresAtUtc);
    }

    public async Task<StorageObservationSnapshot?> LoadFreshSnapshotAsync(StorageObservationContext context,
        CancellationToken ct = default)
    {
        Validate(context);
        var outcome = await RunAsync<StorageObservationSnapshotOutcome>(async (tx, token) =>
            {
                if (!await FenceAsync(context, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.TargetUnavailable));
                if (!await HasDemandAsync(context.DeploymentTargetId, tx.Session, token))
                    return tx.Abort(Rejected(StorageObservationRejection.NoDemand));
                var control = await controls.Find(tx.Session, HealthyOwner(context)).FirstOrDefaultAsync(token);
                if (control is null)
                    return tx.Abort(Rejected(StorageObservationRejection.LeaseLost));
                var snapshot = await snapshots.Find(tx.Session, FreshSnapshot(control)).FirstOrDefaultAsync(token);
                if (snapshot is null || !snapshot.Inventory.Complete)
                    return tx.Abort(Rejected(StorageObservationRejection.Superseded));
                if (await DeadlineAsync(new(control.Id, control.InstanceId!, control.Token!, control.Epoch),
                        context,
                        control,
                        tx.Session,
                        token) is { } rejected)
                    return tx.Abort(rejected);
                return tx.Commit(StorageObservationValueMappings.Load(snapshot));
            },
            ct);
        return outcome switch
        {
            StorageObservationSnapshot snapshot => snapshot,
            StorageObservationRejected => null
        };
    }

    private async Task<bool> FenceAsync(StorageObservationContext context,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await fenceTarget(new(context.DeploymentTargetId, context.ConnectionVersion), session, ct)).Value is
        DeploymentTargetObservationFenced;

    private async Task<StorageObservationControlDocument> GetOrCreateAsync(StorageObservationContext context,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var control = await controls.Find(session, Control(context.DeploymentTargetId)).FirstOrDefaultAsync(ct);
        if (control is not null)
            return control;
        control = new(context.DeploymentTargetId,
            context.ConnectionVersion,
            0,
            0,
            0,
            0,
            null,
            null,
            DateTime.UnixEpoch,
            false,
            null,
            null,
            DateTime.UnixEpoch);
        try
        {
            await controls.InsertOneAsync(session, control, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new ControlCreationRaceException();
        }

        return control;
    }

    private Task TouchAsync(StorageObservationControlDocument control,
        IClientSessionHandle session,
        CancellationToken ct) => controls.UpdateOneAsync(session,
        Control(control.Id),
        ControlPipeline(new BsonDocument()),
        cancellationToken: ct);

    private Task<bool> HasDemandAsync(string target, IClientSessionHandle session, CancellationToken ct) =>
        demands.Find(session, DemandScope(target) & DemandExpiry("$gt")).AnyAsync(ct);

    private async Task MarkIdleAsync(StorageObservationControlDocument control,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (await HasDemandAsync(control.Id, session, ct))
            return;
        var fields = new BsonDocument
            { { "IdleSinceUtc", "$$NOW" }, { "ObservationVersion", Increment("ObservationVersion") } };
        Invalidate(fields);
        await controls.UpdateOneAsync(session,
            Control(control.Id) & NonIdle(),
            ControlPipeline(fields),
            cancellationToken: ct);
        await snapshots.UpdateOneAsync(session,
            Snapshot(control.Id),
            SnapshotPipeline(new BsonDocument("ExpiresAtUtc",
                new BsonDocument("$ifNull", new BsonArray { "$ExpiresAtUtc", ServerAfter(300) }))),
            cancellationToken: ct);
    }

    private Task PublishAsync(StorageObservationControlDocument control,
        IClientSessionHandle session,
        CancellationToken ct) => storeOutboxWrites(session,
        [
            OutboxWrite.Create(new StorageObservationChangedEvent(control.Id,
                control.ConnectionVersion,
                control.ObservationVersion))
        ],
        ct);

    private async Task<StorageObservationRejected?> DeadlineAsync(StorageObservationLease lease,
        StorageObservationContext context,
        StorageObservationControlDocument expected,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!await HasDemandAsync(lease.DeploymentTargetId, session, ct))
            return Rejected(StorageObservationRejection.NoDemand);
        var fenced = await controls.UpdateOneAsync(session,
            CommitDeadline(lease, context, expected),
            ControlPipeline(new BsonDocument()),
            cancellationToken: ct);
        return fenced.MatchedCount == 1 ? null : Rejected(StorageObservationRejection.LeaseLost);
    }

    private async Task<T> RunAsync<T>(
        Func<MongoTransactionContext<T>, CancellationToken, Task<MongoTransactionDecision<T>>> action,
        CancellationToken ct) where T : notnull
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

    private static StorageObservationRejected Rejected(StorageObservationRejection reason) => new(reason);

    private static FindOneAndUpdateOptions<StorageObservationControlDocument> AfterControl() =>
        new()
        {
            ReturnDocument = ReturnDocument.After
        };

    private static BsonDocument Literal(BsonValue value) => new("$literal", value);
    private static BsonDocument Literal(string value) => Literal(new BsonString(value));
    private static BsonDocument Literal(DateTime value) => Literal(new BsonDateTime(value));
    private static BsonDocument Increment(string field) => new("$add", new BsonArray { "$" + field, 1L });

    private static UpdateDefinition<StorageObservationControlDocument> ControlPipeline(BsonDocument fields)
    {
        fields.Add("DemandFence", Increment("DemandFence"));
        fields.Add("UpdatedAtUtc", "$$NOW");
        return Builders<StorageObservationControlDocument>.Update.Pipeline(
            PipelineDefinition<StorageObservationControlDocument, StorageObservationControlDocument>.Create([
                new BsonDocument("$set", fields)
            ]));
    }

    private static UpdateDefinition<StorageObservationDemandDocument> DemandPipeline(BsonDocument fields) =>
        Builders<StorageObservationDemandDocument>.Update.Pipeline(
            PipelineDefinition<StorageObservationDemandDocument, StorageObservationDemandDocument>.Create([
                new BsonDocument("$set", fields)
            ]));

    private static UpdateDefinition<StorageObservationSnapshotDocument> SnapshotPipeline(BsonDocument fields) =>
        Builders<StorageObservationSnapshotDocument>.Update.Pipeline(
            PipelineDefinition<StorageObservationSnapshotDocument, StorageObservationSnapshotDocument>.Create([
                new BsonDocument("$set", fields)
            ]));

    private static void Invalidate(BsonDocument fields)
    {
        fields.Add("Epoch", Increment("Epoch"));
        fields.Add("SourceControlVersion", Increment("SourceControlVersion"));
        fields.Add("InstanceId", BsonNull.Value);
        fields.Add("Token", BsonNull.Value);
        fields.Add("LeaseExpiresAtUtc", Literal(DateTime.UnixEpoch));
        fields.Add("SourceHealthy", false);
        fields.Add("SourceId", BsonNull.Value);
    }

    private static void Validate(StorageObservationContext context)
    {
        ValidateId(context.DeploymentTargetId);
        if (context.ConnectionVersion < 1)
            throw new ArgumentException("A positive connection version is required.");
    }

    private static void Validate(StorageObservationDemandIdentity identity)
    {
        ValidateId(identity.DeploymentTargetId);
        ValidateOpaque(identity.SubscriptionId);
        ValidateOpaque(identity.ConnectionId);
        ValidateOpaque(identity.SessionId);
    }

    private static void Validate(StorageObservationLease lease)
    {
        ValidateId(lease.DeploymentTargetId);
        ValidateOpaque(lease.InstanceId);
        ValidateOpaque(lease.Token);
        if (lease.Epoch < 1)
            throw new ArgumentException("A positive owner epoch is required.");
    }

    private static void Validate(StorageObservationLease lease, StorageObservationContext context)
    {
        Validate(lease);
        Validate(context);
        if (lease.DeploymentTargetId != context.DeploymentTargetId)
            throw new ArgumentException("Lease and context must have the same target.");
    }

    private static void ValidateId(string value)
    {
        if (!ObjectId.TryParse(value, out var id) || id.ToString() != value)
            throw new ArgumentException("A canonical target identity is required.");
    }

    private static void ValidateOpaque(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            throw new ArgumentException("A bounded observation identity is required.");
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limit));
    }
}
