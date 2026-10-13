using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using static Pole.Features.ResourceDeployments.Application.Persistence.Observation.ObservationStoreFilters;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Observation;

/// <summary>Durable demand, ownership and sanitized snapshots shared by API replicas.</summary>
internal sealed class MongoResourceDeploymentObservationStore(
    IMongoDatabase database,
    MongoRunTransaction transactions,
    FenceDeploymentTargetObservation fenceTarget,
    StoreOutboxWrites storeOutboxWrites) : IResourceDeploymentObservationStore
{
    internal const string ControlsCollection = "ResourceDeploymentObservationControls";
    internal const string DemandsCollection = "ResourceDeploymentObservationDemands";
    internal const string SnapshotsCollection = "ResourceDeploymentObservationSnapshots";
    private readonly IMongoCollection<ObservationControlDocument> controls = database.GetCollection<ObservationControlDocument>(ControlsCollection);
    private readonly IMongoCollection<ObservationDemandDocument> demands = database.GetCollection<ObservationDemandDocument>(DemandsCollection);
    private readonly IMongoCollection<ObservationSnapshotDocument> snapshots = database.GetCollection<ObservationSnapshotDocument>(SnapshotsCollection);
    private readonly IMongoCollection<ResourceDeployment> deployments = database.GetCollection<ResourceDeployment>("ResourceDeployments");

    public Task<ObservationMutationOutcome> PutDemandAsync(ObservationDemand demand, ResourceDeploymentObservationContext context, CancellationToken ct = default) =>
        PutOrRenewDemandAsync(demand, context, ct);

    public Task<ObservationMutationOutcome> RenewDemandAsync(ObservationDemand demand, ResourceDeploymentObservationContext context, CancellationToken ct = default) =>
        PutOrRenewDemandAsync(demand, context, ct);

    private Task<ObservationMutationOutcome> PutOrRenewDemandAsync(ObservationDemand demand, ResourceDeploymentObservationContext context, CancellationToken ct)
    {
        Validate(context);
        Validate(demand.Identity);
        if (demand.Identity.TenantId != context.Deployment.TenantId || demand.Identity.DeploymentId != context.Deployment.Id)
            throw new ArgumentException("Observation demand must belong to its deployment context.", nameof(demand));
        return RunAsync<ObservationMutationOutcome>(async (transaction, token) =>
        {
            var rejection = await FenceContextAsync(context, transaction.Session, token);
            if (rejection is not null) return transaction.Abort(rejection);
            var control = await GetOrCreateControlAsync(context, transaction.Session, token);
            await TouchControlAsync(control, transaction.Session, token);
            var key = DemandKey(demand.Identity);
            var previous = await demands.Find(transaction.Session, DemandByKey(key)).FirstOrDefaultAsync(token);
            if (previous is not null && (previous.ConnectionId != demand.Identity.ConnectionId || previous.SessionId != demand.Identity.SessionId))
                return transaction.Abort(Rejected(ObservationStoreRejection.Superseded));

            var originalBound = new BsonDocument("$min", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$OriginalExpiresAtUtc", Literal(demand.ExpiresAtUtc.UtcDateTime) }),
                Literal(demand.ExpiresAtUtc.UtcDateTime)
            });
            await demands.UpdateOneAsync(transaction.Session, DemandByKey(key), DemandPipeline(new BsonDocument
            {
                { "TenantId", Literal(new BsonObjectId(ObjectId.Parse(demand.Identity.TenantId))) },
                { "DeploymentId", Literal(new BsonObjectId(ObjectId.Parse(demand.Identity.DeploymentId))) },
                { "SubscriptionId", Literal(demand.Identity.SubscriptionId) },
                { "ConnectionId", Literal(demand.Identity.ConnectionId) },
                { "SessionId", Literal(demand.Identity.SessionId) },
                { "OriginalExpiresAtUtc", originalBound },
                { "ExpiresAtUtc", new BsonDocument("$min", new BsonArray { originalBound, ServerAfter(60) }) }
            }), new UpdateOptions { IsUpsert = true }, token);
            if (!await demands.Find(transaction.Session, DemandByKey(key) & ServerExpiry("ExpiresAtUtc", "$gt")).AnyAsync(token))
                return transaction.Abort(Rejected(ObservationStoreRejection.NoDemand));

            var fields = new BsonDocument { { "IdleSinceUtc", BsonNull.Value } };
            var pin = ObservationValueMappings.Pin(context);
            if (control.Pin != pin)
            {
                AddInvalidation(fields);
                fields.Add("ObservationVersion", Increment("ObservationVersion"));
                fields.Add("Pin", Literal(pin.ToBsonDocument()));
            }
            var updated = await controls.FindOneAndUpdateAsync(transaction.Session, ControlIdentity(control.TenantId, control.Id), ControlPipeline(fields),
                new FindOneAndUpdateOptions<ObservationControlDocument> { ReturnDocument = ReturnDocument.After }, token);
            if (control.Pin != pin) await PublishControlChangeAsync(updated, transaction.Session, token);
            await snapshots.UpdateOneAsync(transaction.Session, SnapshotIdentity(control.TenantId, control.Id),
                Builders<ObservationSnapshotDocument>.Update.Set(item => item.ExpiresAtUtc, null), cancellationToken: token);
            return transaction.Commit(new ObservationMutationAccepted());
        }, ct);
    }

    public Task<ObservationMutationOutcome> RemoveDemandAsync(ObservationDemandIdentity identity, CancellationToken ct = default)
    {
        Validate(identity);
        return RunAsync<ObservationMutationOutcome>(async (transaction, token) =>
        {
            var control = await controls.Find(transaction.Session, ControlIdentity(identity.TenantId, identity.DeploymentId)).FirstOrDefaultAsync(token);
            if (control is null) return transaction.Commit(new ObservationMutationAccepted());
            await TouchControlAsync(control, transaction.Session, token);
            await demands.DeleteOneAsync(transaction.Session, DemandIdentity(identity), cancellationToken: token);
            await MarkIdleIfNoDemandAsync(control, transaction.Session, token);
            return transaction.Commit(new ObservationMutationAccepted());
        }, ct);
    }

    public async Task<ObservationDemandPage> ScanDemandsAsync(string? after, int limit, CancellationToken ct = default)
    {
        ValidateLimit(limit);
        if (after is not null) ValidateObjectId(after);
        var rows = await demands.Aggregate().Match(ActiveDemandsAfter(after)).Group(new BsonDocument
            {
                { "_id", "$DeploymentId" }, { "TenantId", new BsonDocument("$first", "$TenantId") }
            }).Sort(new BsonDocument("_id", 1)).Limit(limit + 1).ToListAsync(ct);
        var selected = rows.Take(limit).Select(row => new ObservationIdentity(row["TenantId"].AsObjectId.ToString(), row["_id"].AsObjectId.ToString())).ToArray();
        return new(selected, rows.Count > limit ? selected[^1].DeploymentId : null);
    }

    public async Task ReapExpiredDemandsAsync(int limit, CancellationToken ct = default)
    {
        ValidateLimit(limit);
        // TTL can delete a demand before this pass. Retained non-idle controls are the authoritative scan.
        var candidates = await controls.Find(NonIdleControls()).SortBy(item => item.UpdatedAtUtc).Limit(limit).ToListAsync(ct);
        foreach (var candidate in candidates)
            await RunAsync<ObservationMutationOutcome>(async (transaction, token) =>
            {
                var control = await controls.Find(transaction.Session, ControlIdentity(candidate.TenantId, candidate.Id)).FirstOrDefaultAsync(token);
                if (control is null || control.IdleSinceUtc is not null) return transaction.Commit(new ObservationMutationAccepted());
                await TouchControlAsync(control, transaction.Session, token);
                await demands.DeleteManyAsync(transaction.Session,
                    DemandScope(control.TenantId, control.Id) & ServerExpiry("ExpiresAtUtc", "$lte"), cancellationToken: token);
                await MarkIdleIfNoDemandAsync(control, transaction.Session, token);
                return transaction.Commit(new ObservationMutationAccepted());
            }, ct);
    }

    public Task<ObservationLeaseOutcome> ClaimLeaseAsync(ResourceDeploymentObservationContext context, string instanceId, CancellationToken ct = default)
    {
        Validate(context);
        ValidateOpaque(instanceId);
        return RunAsync<ObservationLeaseOutcome>(async (transaction, token) =>
        {
            var rejection = await FenceContextAsync(context, transaction.Session, token);
            if (rejection is not null) return transaction.Abort(rejection);
            var identity = new ObservationIdentity(context.Deployment.TenantId, context.Deployment.Id);
            if (!await HasDemandAsync(identity, transaction.Session, token)) return transaction.Abort(Rejected(ObservationStoreRejection.NoDemand));
            var control = await GetOrCreateControlAsync(context, transaction.Session, token);
            var pin = ObservationValueMappings.Pin(context);
            if (control.Pin == pin && await controls.Find(transaction.Session, ActiveOwner(control.TenantId, control.Id)).AnyAsync(token))
                return transaction.Abort(Rejected(ObservationStoreRejection.Busy));
            var leaseToken = Guid.NewGuid().ToString("N");
            var claimFields = new BsonDocument
            {
                { "Pin", Literal(pin.ToBsonDocument()) }, { "InstanceId", Literal(instanceId) }, { "Token", Literal(leaseToken) },
                { "Epoch", Increment("Epoch") }, { "SourceControlVersion", Increment("SourceControlVersion") },
                { "LeaseExpiresAtUtc", ServerAfter(30) }, { "SourceHealthy", false }, { "BindingId", BsonNull.Value }, { "IdleSinceUtc", BsonNull.Value }
            };
            var withdrawCoverage = control.SourceHealthy || control.BindingId is not null;
            if (withdrawCoverage) claimFields.Add("ObservationVersion", Increment("ObservationVersion"));
            var claimed = await controls.FindOneAndUpdateAsync(transaction.Session,
                ControlAtEpoch(control),
                ControlPipeline(claimFields), new FindOneAndUpdateOptions<ObservationControlDocument> { ReturnDocument = ReturnDocument.After }, token);
            if (claimed is null) return transaction.Abort(Rejected(ObservationStoreRejection.LeaseLost));
            if (withdrawCoverage) await PublishControlChangeAsync(claimed, transaction.Session, token);
            var lease = new ObservationLease(claimed.TenantId, claimed.Id, instanceId, leaseToken, claimed.Epoch);
            var deadline = await FenceCommitDeadlineAsync(lease, context, claimed, transaction.Session, token);
            if (deadline is not null) return transaction.Abort(deadline);
            return transaction.Commit(new ObservationOwnership(lease, claimed.SourceControlVersion));
        }, ct);
    }

    public Task<ObservationMutationOutcome> RenewLeaseAsync(ObservationLease lease, ResourceDeploymentObservationContext context, CancellationToken ct = default)
    {
        Validate(lease, context);
        return RunAsync<ObservationMutationOutcome>(async (transaction, token) =>
        {
            var rejection = await FenceContextAsync(context, transaction.Session, token);
            if (rejection is not null) return transaction.Abort(rejection);
            if (!await HasDemandAsync(new(lease.TenantId, lease.DeploymentId), transaction.Session, token))
                return transaction.Abort(Rejected(ObservationStoreRejection.NoDemand));
            var updated = await controls.UpdateOneAsync(transaction.Session, ActiveLease(lease) & PinFilter(context),
                ControlPipeline(new BsonDocument("LeaseExpiresAtUtc", ServerAfter(30))), cancellationToken: token);
            return updated.MatchedCount == 1 ? transaction.Commit(new ObservationMutationAccepted()) : transaction.Abort(Rejected(ObservationStoreRejection.LeaseLost));
        }, ct);
    }

    public Task<ObservationMutationOutcome> ReleaseLeaseAsync(ObservationLease lease, CancellationToken ct = default)
    {
        Validate(lease);
        return RunAsync<ObservationMutationOutcome>(async (transaction, token) =>
        {
            var fields = new BsonDocument();
            AddInvalidation(fields);
            fields.Add("ObservationVersion", Increment("ObservationVersion"));
            var updated = await controls.FindOneAndUpdateAsync(transaction.Session, LeaseIdentity(lease), ControlPipeline(fields),
                new FindOneAndUpdateOptions<ObservationControlDocument> { ReturnDocument = ReturnDocument.After }, token);
            if (updated is null) return transaction.Abort(Rejected(ObservationStoreRejection.LeaseLost));
            await PublishControlChangeAsync(updated, transaction.Session, token);
            await MarkIdleIfNoDemandAsync(updated, transaction.Session, token);
            return transaction.Commit(new ObservationMutationAccepted());
        }, ct);
    }

    public Task<ObservationSourceStateOutcome> SetSourceStateAsync(ObservationLease lease, ResourceDeploymentObservationContext context,
        long expectedSourceControlVersion, bool healthy, string? bindingId, CancellationToken ct = default)
    {
        Validate(lease, context);
        if (healthy && string.IsNullOrWhiteSpace(bindingId)) throw new ArgumentException("A healthy source requires its native binding.", nameof(bindingId));
        if (bindingId is not null) ValidateOpaque(bindingId);
        return RunAsync<ObservationSourceStateOutcome>(async (transaction, token) =>
        {
            // Loss of native coverage must remain fenceable after credentials/placement become unavailable.
            // Exact retained lease and pin authorize only withdrawal of this owner's existing coverage.
            if (healthy)
            {
                var rejection = await FenceContextAsync(context, transaction.Session, token);
                if (rejection is not null) return transaction.Abort(rejection);
            }
            if (!await HasDemandAsync(new(lease.TenantId, lease.DeploymentId), transaction.Session, token))
                return transaction.Abort(Rejected(ObservationStoreRejection.NoDemand));
            var control = await controls.Find(transaction.Session, ActiveLease(lease) & PinFilter(context)).FirstOrDefaultAsync(token);
            if (control is null) return transaction.Abort(Rejected(ObservationStoreRejection.LeaseLost));
            if (control.SourceControlVersion != expectedSourceControlVersion) return transaction.Abort(Rejected(ObservationStoreRejection.BindingChanged));
            if (control.SourceHealthy == healthy && control.BindingId == bindingId)
            {
                await TouchControlAsync(control, transaction.Session, token);
                if (healthy)
                {
                    var deadline = await FenceCommitDeadlineAsync(lease, context, control, transaction.Session, token);
                    if (deadline is not null) return transaction.Abort(deadline);
                }
                return transaction.Commit(new ObservationSourceState(control.SourceControlVersion));
            }
            var updated = await controls.FindOneAndUpdateAsync(transaction.Session, SourceAtVersion(lease, context, expectedSourceControlVersion),
                ControlPipeline(new BsonDocument
                {
                    { "SourceControlVersion", Increment("SourceControlVersion") }, { "ObservationVersion", Increment("ObservationVersion") }, { "SourceHealthy", healthy },
                    { "BindingId", bindingId is null ? BsonNull.Value : Literal(bindingId) }
                }), new FindOneAndUpdateOptions<ObservationControlDocument> { ReturnDocument = ReturnDocument.After }, token);
            if (updated is null) return transaction.Abort(Rejected(ObservationStoreRejection.LeaseLost));
            await PublishControlChangeAsync(updated, transaction.Session, token);
            if (healthy)
            {
                var deadline = await FenceCommitDeadlineAsync(lease, context, updated, transaction.Session, token);
                if (deadline is not null) return transaction.Abort(deadline);
            }
            return transaction.Commit(new ObservationSourceState(updated.SourceControlVersion));
        }, ct);
    }

    public Task<ObservationSnapshotOutcome> TryCommitSnapshotAsync(ObservationLease lease, ResourceDeploymentObservationContext context,
        long sourceControlVersion, string bindingId, ResourceDeploymentBoundObservation observation, CancellationToken ct = default)
    {
        Validate(lease, context);
        ValidateOpaque(bindingId);
        if (bindingId != observation.BindingId) return Task.FromResult<ObservationSnapshotOutcome>(Rejected(ObservationStoreRejection.BindingChanged));
        var storedObservation = ObservationValueMappings.Store(observation.Observation);
        var storedDiagnostics = ObservationValueMappings.Store(observation.Diagnostics);
        long? expectedObservationVersion = null;
        return RunAsync<ObservationSnapshotOutcome>(async (transaction, token) =>
        {
            var rejection = await FenceContextAsync(context, transaction.Session, token);
            if (rejection is not null) return transaction.Abort(rejection);
            if (!await HasDemandAsync(new(lease.TenantId, lease.DeploymentId), transaction.Session, token))
                return transaction.Abort(Rejected(ObservationStoreRejection.NoDemand));
            var control = await controls.Find(transaction.Session, ActiveLease(lease) & PinFilter(context)).FirstOrDefaultAsync(token);
            if (control is null) return transaction.Abort(Rejected(ObservationStoreRejection.LeaseLost));
            if (!control.SourceHealthy || control.SourceControlVersion != sourceControlVersion || control.BindingId != bindingId)
                return transaction.Abort(Rejected(ObservationStoreRejection.BindingChanged));
            expectedObservationVersion ??= control.ObservationVersion;
            if (control.ObservationVersion != expectedObservationVersion) return transaction.Abort(Rejected(ObservationStoreRejection.Superseded));
            var updated = await controls.FindOneAndUpdateAsync(transaction.Session,
                SnapshotCommit(lease, context, sourceControlVersion, bindingId, expectedObservationVersion.Value),
                ControlPipeline(new BsonDocument("ObservationVersion", Increment("ObservationVersion"))),
                new FindOneAndUpdateOptions<ObservationControlDocument> { ReturnDocument = ReturnDocument.After }, token);
            if (updated is null) return transaction.Abort(Rejected(ObservationStoreRejection.LeaseLost));
            var snapshot = new ObservationSnapshotDocument(lease.DeploymentId, lease.TenantId, ObservationValueMappings.Pin(context), bindingId,
                sourceControlVersion, updated.ObservationVersion, updated.UpdatedAtUtc, storedObservation, storedDiagnostics,
                ObservationValueMappings.Fingerprint(storedObservation, storedDiagnostics), null);
            await snapshots.ReplaceOneAsync(transaction.Session, SnapshotIdentity(lease.TenantId, lease.DeploymentId), snapshot,
                new ReplaceOptions { IsUpsert = true }, token);
            await PublishControlChangeAsync(updated, transaction.Session, token);
            var deadline = await FenceCommitDeadlineAsync(lease, context, updated, transaction.Session, token);
            if (deadline is not null) return transaction.Abort(deadline);
            return transaction.Commit(ObservationValueMappings.Load(snapshot, context));
        }, ct);
    }

    public async Task<ObservationControlState?> LoadControlAsync(ObservationIdentity identity, CancellationToken ct = default)
    {
        ValidateObjectId(identity.TenantId);
        ValidateObjectId(identity.DeploymentId);
        var control = await controls.Find(ControlIdentity(identity.TenantId, identity.DeploymentId)).FirstOrDefaultAsync(ct);
        return control is null ? null : new(control.Epoch, control.SourceControlVersion, control.ObservationVersion,
            control.SourceHealthy, control.BindingId, control.Token is null ? null : control.LeaseExpiresAtUtc);
    }

    public async Task<ResourceDeploymentSharedObservation?> LoadFreshSnapshotAsync(ResourceDeploymentObservationContext context, CancellationToken ct = default)
    {
        Validate(context);
        var result = await RunAsync<ObservationSnapshotOutcome>(async (transaction, token) =>
        {
            var rejection = await FenceContextAsync(context, transaction.Session, token);
            if (rejection is not null) return transaction.Abort(rejection);
            if (!await HasDemandAsync(new(context.Deployment.TenantId, context.Deployment.Id), transaction.Session, token))
                return transaction.Abort(Rejected(ObservationStoreRejection.NoDemand));
            var control = await controls.Find(transaction.Session, HealthyOwner(context)).FirstOrDefaultAsync(token);
            if (control is null) return transaction.Abort(Rejected(ObservationStoreRejection.LeaseLost));
            var snapshot = await snapshots.Find(transaction.Session, CurrentFreshSnapshot(control)).FirstOrDefaultAsync(token);
            if (snapshot is null || snapshot.Pin != ObservationValueMappings.Pin(context)) return transaction.Abort(Rejected(ObservationStoreRejection.Superseded));
            var deadline = await FenceCommitDeadlineAsync(new(control.TenantId, control.Id, control.InstanceId!, control.Token!, control.Epoch),
                context, control, transaction.Session, token);
            if (deadline is not null) return transaction.Abort(deadline);
            return transaction.Commit(ObservationValueMappings.Load(snapshot, context));
        }, ct);
        return result switch { ResourceDeploymentSharedObservation snapshot => snapshot, ObservationStoreRejected => null };
    }

    private async Task<ObservationStoreRejected?> FenceContextAsync(ResourceDeploymentObservationContext context, IClientSessionHandle session, CancellationToken ct)
    {
        var target = await fenceTarget(new(context.Deployment.Placement.DeploymentTargetId, context.TargetConnectionVersion), session, ct);
        if (target is DeploymentTargetObservationFenceRejected) return Rejected(ObservationStoreRejection.TargetUnavailable);
        var fenced = await deployments.UpdateOneAsync(session, ResourceDeploymentFilters.ForCurrentObservation(context), Builders<ResourceDeployment>.Update.Inc(item => item.ReferenceFence, 1), cancellationToken: ct);
        return fenced.ModifiedCount == 1 ? null : Rejected(ObservationStoreRejection.Superseded);
    }

    private async Task<ObservationControlDocument> GetOrCreateControlAsync(ResourceDeploymentObservationContext context, IClientSessionHandle session, CancellationToken ct)
    {
        var existing = await controls.Find(session, ControlIdentity(context.Deployment.TenantId, context.Deployment.Id)).FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;
        var created = new ObservationControlDocument(context.Deployment.Id, context.Deployment.TenantId, ObservationValueMappings.Pin(context),
            0, 0, 0, 0, null, null, DateTime.UnixEpoch, false, null, null, DateTime.UnixEpoch);
        try { await controls.InsertOneAsync(session, created, cancellationToken: ct); }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { throw new ControlCreationRaceException(); }
        return created;
    }

    private Task TouchControlAsync(ObservationControlDocument control, IClientSessionHandle session, CancellationToken ct) =>
        controls.UpdateOneAsync(session, ControlIdentity(control.TenantId, control.Id), ControlPipeline(new BsonDocument()), cancellationToken: ct);

    private async Task<bool> HasDemandAsync(ObservationIdentity identity, IClientSessionHandle session, CancellationToken ct) =>
        await demands.Find(session, DemandScope(identity.TenantId, identity.DeploymentId) & ServerExpiry("ExpiresAtUtc", "$gt")).AnyAsync(ct);

    private async Task MarkIdleIfNoDemandAsync(ObservationControlDocument control, IClientSessionHandle session, CancellationToken ct)
    {
        if (await HasDemandAsync(new(control.TenantId, control.Id), session, ct)) return;
        var fields = new BsonDocument { { "IdleSinceUtc", "$$NOW" } };
        AddInvalidation(fields);
        fields.Add("ObservationVersion", Increment("ObservationVersion"));
        await controls.UpdateOneAsync(session, NonIdleControl(control),
            ControlPipeline(fields), cancellationToken: ct);
        await snapshots.UpdateOneAsync(session, SnapshotIdentity(control.TenantId, control.Id), SnapshotPipeline(new BsonDocument
        {
            { "ExpiresAtUtc", new BsonDocument("$ifNull", new BsonArray { "$ExpiresAtUtc", ServerAfter(300) }) }
        }), cancellationToken: ct);
    }

    private Task PublishControlChangeAsync(ObservationControlDocument control, IClientSessionHandle session, CancellationToken ct) =>
        storeOutboxWrites(session, [OutboxWrite.Create(new ResourceDeploymentObservationChangedEvent(
            control.Id, control.Pin.ResourceId, control.Pin.ResourceGroupId,
            control.Pin.DeploymentRevision, control.Pin.DesiredGeneration, control.ObservationVersion)
        { EventCore = EventCore.Create(control.TenantId) })], ct);

    private async Task<ObservationStoreRejected?> FenceCommitDeadlineAsync(ObservationLease lease, ResourceDeploymentObservationContext context,
        ObservationControlDocument expected, IClientSessionHandle session, CancellationToken ct)
    {
        // An outbox/storage await can cross a clock deadline without any competing writer.
        // Recheck the server clock at the final control write before returning a commit decision.
        if (!await HasDemandAsync(new(lease.TenantId, lease.DeploymentId), session, ct)) return Rejected(ObservationStoreRejection.NoDemand);
        var fenced = await controls.UpdateOneAsync(session, CommitDeadline(lease, context, expected),
            ControlPipeline(new BsonDocument()), cancellationToken: ct);
        return fenced.MatchedCount == 1 ? null : Rejected(ObservationStoreRejection.LeaseLost);
    }

    private async Task<T> RunAsync<T>(Func<MongoTransactionContext<T>, CancellationToken, Task<MongoTransactionDecision<T>>> action, CancellationToken ct) where T : notnull
    {
        for (var attempt = 0; ; attempt++)
            try { return await transactions.InvokeOutcomeAsync(action, ct); }
            catch (ControlCreationRaceException) when (attempt < 3) { }
    }

    private sealed class ControlCreationRaceException : Exception;
    private static ObservationStoreRejected Rejected(ObservationStoreRejection reason) => new(reason);
    private static BsonDocument Literal(BsonValue value) => new("$literal", value);
    private static BsonDocument Literal(string value) => Literal(new BsonString(value));
    private static BsonDocument Literal(DateTime value) => Literal(new BsonDateTime(value));
    private static BsonDocument Increment(string field) => new("$add", new BsonArray { "$" + field, 1L });
    private static UpdateDefinition<ObservationControlDocument> ControlPipeline(BsonDocument fields)
    {
        fields.Add("DemandFence", Increment("DemandFence"));
        fields.Add("UpdatedAtUtc", "$$NOW");
        return Builders<ObservationControlDocument>.Update.Pipeline(PipelineDefinition<ObservationControlDocument, ObservationControlDocument>.Create([new BsonDocument("$set", fields)]));
    }
    private static UpdateDefinition<ObservationDemandDocument> DemandPipeline(BsonDocument fields) =>
        Builders<ObservationDemandDocument>.Update.Pipeline(PipelineDefinition<ObservationDemandDocument, ObservationDemandDocument>.Create([new BsonDocument("$set", fields)]));
    private static UpdateDefinition<ObservationSnapshotDocument> SnapshotPipeline(BsonDocument fields) =>
        Builders<ObservationSnapshotDocument>.Update.Pipeline(PipelineDefinition<ObservationSnapshotDocument, ObservationSnapshotDocument>.Create([new BsonDocument("$set", fields)]));
    private static void AddInvalidation(BsonDocument fields)
    {
        fields.Add("Epoch", Increment("Epoch"));
        fields.Add("SourceControlVersion", Increment("SourceControlVersion"));
        fields.Add("InstanceId", BsonNull.Value);
        fields.Add("Token", BsonNull.Value);
        fields.Add("LeaseExpiresAtUtc", Literal(DateTime.UnixEpoch));
        fields.Add("SourceHealthy", false);
        fields.Add("BindingId", BsonNull.Value);
    }
    private static void Validate(ResourceDeploymentObservationContext context)
    {
        ValidateObjectId(context.Deployment.TenantId);
        ValidateObjectId(context.Deployment.Id);
        if (context.DeploymentRevision < 1 || context.TargetConnectionVersion < 1 || context.Deployment.DesiredGeneration < 1)
            throw new ArgumentException("Observation context versions must be positive.", nameof(context));
    }
    private static void Validate(ObservationLease lease)
    {
        ValidateObjectId(lease.TenantId);
        ValidateObjectId(lease.DeploymentId);
        ValidateOpaque(lease.InstanceId);
        ValidateOpaque(lease.Token);
        if (lease.Epoch < 1) throw new ArgumentException("An observation lease epoch must be positive.", nameof(lease));
    }
    private static void Validate(ObservationLease lease, ResourceDeploymentObservationContext context)
    {
        Validate(lease);
        Validate(context);
        if (lease.TenantId != context.Deployment.TenantId || lease.DeploymentId != context.Deployment.Id)
            throw new ArgumentException("Observation lease must belong to its context.", nameof(lease));
    }
    private static void Validate(ObservationDemandIdentity identity)
    {
        ValidateObjectId(identity.TenantId);
        ValidateObjectId(identity.DeploymentId);
        ValidateOpaque(identity.SubscriptionId);
        ValidateOpaque(identity.ConnectionId);
        ValidateOpaque(identity.SessionId);
    }
    private static void ValidateObjectId(string value)
    {
        if (!ObjectId.TryParse(value, out var id) || id.ToString() != value) throw new ArgumentException("A canonical Mongo identity is required.");
    }
    private static void ValidateOpaque(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) throw new ArgumentException("A bounded observation identity is required.");
    }
    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
    }
}
