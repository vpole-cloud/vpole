using Pole.Features.StorageProfiles.Domain.Observation;
using MongoDB.Bson;
using System.Linq.Expressions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Database.Observation;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Events;

namespace Pole.Features.StorageProfiles.Application.Observation;

/// <summary>Feature-owned context fences, sanitized payloads and events; shared store owns the protocol.</summary>
internal sealed class MongoStorageObservationStore(
    IMongoCollection<StorageObservationControlDocument> controls,
    IMongoCollection<StorageObservationDemandDocument> demands,
    IMongoCollection<StorageObservationSnapshotDocument> snapshots,
    MongoRunTransaction transactions,
    FenceDeploymentTargetObservation fenceTarget,
    StoreOutboxWrites storeOutboxWrites,
    StorageObservationOptions options) : IStorageObservationStore
{
    internal const string ControlsCollection = "StorageObservationControls";
    internal const string DemandsCollection = "StorageObservationDemands";
    internal const string SnapshotsCollection = "StorageObservationSnapshots";

    private readonly MongoObservationStore<
        StorageObservationContext,
        StorageObservationControlDocument,
        StorageObservationDemandDocument,
        StorageObservationSnapshotDocument> core = new(controls,
        demands,
        snapshots,
        transactions,
        new Definition(fenceTarget, storeOutboxWrites));

    public Task<StorageObservationMutationOutcome> PutDemandAsync(StorageObservationDemand demand,
        StorageObservationContext context,
        CancellationToken ct = default) => PutOrRenewAsync(demand, context, ct);

    public Task<StorageObservationMutationOutcome> RenewDemandAsync(StorageObservationDemand demand,
        StorageObservationContext context,
        CancellationToken ct = default) => PutOrRenewAsync(demand, context, ct);

    private async Task<StorageObservationMutationOutcome> PutOrRenewAsync(StorageObservationDemand demand,
        StorageObservationContext context,
        CancellationToken ct)
    {
        Validate(context);
        Validate(demand.Identity);
        if (demand.Identity.DeploymentTargetId != context.DeploymentTargetId)
            throw new ArgumentException("Observation demand must belong to its context.", nameof(demand));
        return Mutation(await core.PutDemandAsync(Demand(demand.Identity, demand.ExpiresAtUtc), context, ct));
    }

    public async Task<StorageObservationMutationOutcome> RemoveDemandAsync(StorageObservationDemandIdentity identity,
        CancellationToken ct = default)
    {
        Validate(identity);
        return Mutation(await core.RemoveDemandAsync(DemandIdentity(identity), ct));
    }

    public async Task<StorageObservationDemandPage> ScanDemandsAsync(string? after,
        int limit,
        CancellationToken ct = default)
    {
        ValidateLimit(limit);
        if (after is not null)
            ValidateId(after);
        var page = await core.ScanAsync(after, limit, ct);
        return new(page.Items.Select(item => item.Id).ToArray(), page.NextCursor);
    }

    public Task ReapExpiredDemandsAsync(int limit, CancellationToken ct = default)
    {
        ValidateLimit(limit);
        return core.ReapAsync(limit, ct);
    }

    public async Task<StorageObservationLeaseOutcome> ClaimLeaseAsync(StorageObservationContext context,
        string instanceId,
        CancellationToken ct = default)
    {
        Validate(context);
        ValidateOpaque(instanceId);
        return (await core.ClaimAsync(context, instanceId, ct)) switch
        {
            MongoObservationSuccess<StorageObservationControlDocument> { Value: var control } =>
                new StorageObservationOwnership(
                new(control.Id,
                    instanceId,
                    control.Token!,
                    control.Epoch),
                control.SourceControlVersion),
            MongoObservationRejected rejection => Map(rejection)
        };
    }

    public async Task<StorageObservationMutationOutcome> RenewLeaseAsync(StorageObservationLease lease,
        StorageObservationContext context,
        CancellationToken ct = default)
    {
        Validate(lease, context);
        return Mutation(await core.RenewAsync(Lease(lease), context, ct));
    }

    public async Task<StorageObservationMutationOutcome> ReleaseLeaseAsync(StorageObservationLease lease,
        CancellationToken ct = default)
    {
        Validate(lease);
        return Mutation(await core.ReleaseAsync(Lease(lease), ct));
    }

    public async Task<StorageObservationSourceOutcome> SetSourceStateAsync(StorageObservationLease lease,
        StorageObservationContext context,
        long expectedSourceVersion,
        bool healthy,
        string? sourceId,
        CancellationToken ct = default)
    {
        Validate(lease, context);
        if (healthy && string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("Healthy coverage requires a source identity.");
        if (sourceId is not null)
            ValidateOpaque(sourceId);
        return (await core.SetSourceAsync(Lease(lease),
                context,
                expectedSourceVersion,
                healthy ? sourceId : null,
                ct)) switch
        {
            MongoObservationSuccess<StorageObservationControlDocument> { Value: var control } =>
                new StorageObservationSourceState(control.SourceControlVersion),
            MongoObservationRejected rejection => Map(rejection)
        };
    }

    public async Task<StorageObservationSnapshotOutcome> TryCommitSnapshotAsync(StorageObservationLease lease,
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
            return new StorageObservationRejected(StorageObservationRejection.InventoryIncomplete);
        return (await core.CommitSnapshotAsync(Lease(lease),
                context,
                sourceVersion,
                sourceId,
                control =>
                    new StorageObservationSnapshotDocument(context.DeploymentTargetId,
                        context.ConnectionVersion,
                        sourceId,
                        sourceVersion,
                        control.ObservationVersion,
                        control.UpdatedAtUtc,
                        StorageObservationValueMappings.Store(safe),
                        null),
                ct)) switch
        {
            MongoObservationSuccess<StorageObservationSnapshotDocument> { Value: var document } =>
                StorageObservationValueMappings.Load(document),
            MongoObservationRejected rejection => Map(rejection)
        };
    }

    public async Task<StorageObservationControlState?> LoadControlAsync(string targetId, CancellationToken ct = default)
    {
        ValidateId(targetId);
        var control = await core.LoadControlAsync(new(targetId, null), ct);
        if (control is null)
            return null;
        return new(control.ConnectionVersion,
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
        var document = await core.LoadFreshAsync(context, ct);
        return document is null
            ? null
            : StorageObservationValueMappings.Load(document);
    }

    private static MongoObservationDemand Demand(StorageObservationDemandIdentity identity, DateTimeOffset expires) =>
        new(DemandIdentity(identity), expires);

    private static MongoObservationDemandIdentity DemandIdentity(StorageObservationDemandIdentity identity) =>
        new(new(identity.DeploymentTargetId, null),
            identity.SubscriptionId,
            identity.ConnectionId,
            identity.SessionId);

    private static MongoObservationLease Lease(StorageObservationLease lease) => new(
        new(lease.DeploymentTargetId, null),
        lease.InstanceId,
        lease.Token,
        lease.Epoch);

    private static StorageObservationMutationOutcome Mutation<T>(MongoObservationOutcome<T> outcome)
        where T : notnull => outcome switch
        {
            MongoObservationSuccess<T> => new StorageObservationMutationAccepted(),
            MongoObservationRejected rejection => Map(rejection)
        };

    private static StorageObservationRejected Map(MongoObservationRejected rejection) => new(rejection.Reason switch
    {
        MongoObservationRejection.Busy => StorageObservationRejection.Busy,
        MongoObservationRejection.NoDemand => StorageObservationRejection.NoDemand,
        MongoObservationRejection.LeaseLost => StorageObservationRejection.LeaseLost,
        MongoObservationRejection.Superseded => StorageObservationRejection.Superseded,
        MongoObservationRejection.SourceChanged => StorageObservationRejection.SourceChanged,
        MongoObservationRejection.TargetUnavailable => StorageObservationRejection.TargetUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(rejection))
    });

    private sealed class Definition(FenceDeploymentTargetObservation fenceTarget, StoreOutboxWrites storeOutboxWrites)
        : IMongoObservationDefinition<
            StorageObservationContext,
            StorageObservationControlDocument,
            StorageObservationDemandDocument,
            StorageObservationSnapshotDocument>
    {
        public MongoObservationScope Scope(StorageObservationContext context) =>
            new(context.DeploymentTargetId, null);

        public MongoObservationScope ScopeOf(StorageObservationControlDocument control) => new(control.Id, null);
        public Expression<Func<StorageObservationDemandDocument, string>> DemandSubject =>
            item => item.DeploymentTargetId;
        public Expression<Func<StorageObservationControlDocument, string?>> Source => item => item.SourceId;
        public Expression<Func<StorageObservationSnapshotDocument, string>> SnapshotSource => item => item.SourceId;

        public FilterDefinition<StorageObservationControlDocument> ControlScope(MongoObservationScope scope) =>
            StorageObservationFilters.ForControlScope(scope);

        public FilterDefinition<StorageObservationDemandDocument> DemandScope(MongoObservationScope scope) =>
            StorageObservationFilters.ForDemandScope(scope);

        public FilterDefinition<StorageObservationSnapshotDocument> SnapshotScope(MongoObservationScope scope) =>
            StorageObservationFilters.ForSnapshotScope(scope);

        public FilterDefinition<StorageObservationControlDocument> ControlPin(StorageObservationContext context) =>
            StorageObservationFilters.ForControlContext(context);

        public UpdateDefinition<StorageObservationDemandDocument> DemandIdentityUpdate(MongoObservationScope scope) =>
            Builders<StorageObservationDemandDocument>.Update.Set(item => item.DeploymentTargetId, scope.Id);

        public StorageObservationControlDocument CreateControl(StorageObservationContext context) => new(
            context.DeploymentTargetId,
            context.ConnectionVersion,
            Epoch: 0,
            SourceControlVersion: 0,
            ObservationVersion: 0,
            DemandFence: 0,
            InstanceId: null,
            Token: null,
            LeaseExpiresAtUtc: DateTime.UnixEpoch,
            SourceHealthy: false,
            SourceId: null,
            IdleSinceUtc: null,
            UpdatedAtUtc: DateTime.UnixEpoch);

        public bool HasPin(StorageObservationControlDocument control, StorageObservationContext context) =>
            control.ConnectionVersion == context.ConnectionVersion;

        public UpdateDefinition<StorageObservationControlDocument> PinUpdate(StorageObservationContext context) =>
            Builders<StorageObservationControlDocument>.Update.Set(item => item.ConnectionVersion,
                context.ConnectionVersion);

        public FilterDefinition<StorageObservationSnapshotDocument> SnapshotPin(StorageObservationContext context) =>
            StorageObservationFilters.ForSnapshotContext(context);

        public bool AcceptSnapshot(StorageObservationSnapshotDocument document) => document.Inventory.Complete;

        public async Task<MongoObservationRejected?> FenceAsync(StorageObservationContext context,
            IClientSessionHandle session,
            CancellationToken ct) =>
            (await fenceTarget(new(context.DeploymentTargetId, context.ConnectionVersion), session, ct)).Value is
            DeploymentTargetObservationFenced
                ? null
                : new(MongoObservationRejection.TargetUnavailable);

        public Task PublishAsync(StorageObservationControlDocument control,
            IClientSessionHandle session,
            CancellationToken ct) => storeOutboxWrites(session,
            [
                OutboxWrite.Create(new StorageObservationChangedEvent(control.Id,
                    control.ConnectionVersion,
                    control.ObservationVersion))
            ],
            ct);
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
