using Pole.Features.ResourceDeployments.Domain.Observation;
using MongoDB.Bson;
using System.Linq.Expressions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Database.Observation;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Observation;

/// <summary>Feature-owned context fences, sanitized payloads and events; shared store owns the protocol.</summary>
internal sealed class MongoResourceDeploymentObservationStore(
    IMongoDatabase database,
    IMongoCollection<ResourceDeployment> deployments,
    MongoRunTransaction transactions,
    FenceDeploymentTargetObservation fenceTarget,
    StoreOutboxWrites storeOutboxWrites) : IResourceDeploymentObservationStore
{
    internal const string ControlsCollection = "ResourceDeploymentObservationControls";
    internal const string DemandsCollection = "ResourceDeploymentObservationDemands";
    internal const string SnapshotsCollection = "ResourceDeploymentObservationSnapshots";

    private readonly MongoObservationStore<
        ResourceDeploymentObservationContext,
        ObservationControlDocument,
        ObservationDemandDocument,
        ObservationSnapshotDocument> core = new(database,
        transactions,
        ControlsCollection,
        DemandsCollection,
        SnapshotsCollection,
        new Definition(database, deployments, fenceTarget, storeOutboxWrites));

    public Task<ObservationMutationOutcome> PutDemandAsync(ObservationDemand demand,
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default) => PutOrRenewAsync(demand, context, ct);

    public Task<ObservationMutationOutcome> RenewDemandAsync(ObservationDemand demand,
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default) => PutOrRenewAsync(demand, context, ct);

    private async Task<ObservationMutationOutcome> PutOrRenewAsync(ObservationDemand demand,
        ResourceDeploymentObservationContext context,
        CancellationToken ct)
    {
        Validate(context);
        Validate(demand.Identity);
        if (demand.Identity.TenantId != context.Deployment.TenantId ||
            demand.Identity.DeploymentId != context.Deployment.Id)
            throw new ArgumentException("Observation demand must belong to its context.", nameof(demand));
        return Mutation(await core.PutDemandAsync(Demand(demand.Identity, demand.ExpiresAtUtc), context, ct));
    }

    public async Task<ObservationMutationOutcome> RemoveDemandAsync(ObservationDemandIdentity identity,
        CancellationToken ct = default)
    {
        Validate(identity);
        return Mutation(await core.RemoveDemandAsync(DemandIdentity(identity), ct));
    }

    public async Task<ObservationDemandPage> ScanDemandsAsync(string? after, int limit, CancellationToken ct = default)
    {
        ValidateLimit(limit);
        if (after is not null)
            ValidateObjectId(after);
        var page = await core.ScanAsync(after, limit, ct);
        return new(page.Items.Select(item => new ObservationIdentity(item.TenantId!, item.Id)).ToArray(),
            page.NextCursor);
    }

    public Task ReapExpiredDemandsAsync(int limit, CancellationToken ct = default)
    {
        ValidateLimit(limit);
        return core.ReapAsync(limit, ct);
    }

    public async Task<ObservationLeaseOutcome> ClaimLeaseAsync(ResourceDeploymentObservationContext context,
        string instanceId,
        CancellationToken ct = default)
    {
        Validate(context);
        ValidateOpaque(instanceId);
        return (await core.ClaimAsync(context, instanceId, ct)) switch
        {
            MongoObservationSuccess<ObservationControlDocument> { Value: var control } =>
                new ObservationOwnership(
                new(context.Deployment.TenantId,
                    context.Deployment.Id,
                    instanceId,
                    control.Token!,
                    control.Epoch),
                control.SourceControlVersion),
            MongoObservationRejected rejection => Map(rejection)
        };
    }

    public async Task<ObservationMutationOutcome> RenewLeaseAsync(ObservationLease lease,
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default)
    {
        Validate(lease, context);
        return Mutation(await core.RenewAsync(Lease(lease), context, ct));
    }

    public async Task<ObservationMutationOutcome> ReleaseLeaseAsync(ObservationLease lease,
        CancellationToken ct = default)
    {
        Validate(lease);
        return Mutation(await core.ReleaseAsync(Lease(lease), ct));
    }

    public async Task<ObservationSourceStateOutcome> SetSourceStateAsync(ObservationLease lease,
        ResourceDeploymentObservationContext context,
        long expectedSourceControlVersion,
        bool healthy,
        string? bindingId,
        CancellationToken ct = default)
    {
        Validate(lease, context);
        if (healthy && string.IsNullOrWhiteSpace(bindingId))
            throw new ArgumentException("Healthy coverage requires a source identity.");
        if (bindingId is not null)
            ValidateOpaque(bindingId);
        return (await core.SetSourceAsync(Lease(lease),
                context,
                expectedSourceControlVersion,
                healthy ? bindingId : null,
                ct)) switch
        {
            MongoObservationSuccess<ObservationControlDocument> { Value: var control } =>
                new ObservationSourceState(control.SourceControlVersion),
            MongoObservationRejected rejection => Map(rejection)
        };
    }

    public async Task<ObservationSnapshotOutcome> TryCommitSnapshotAsync(ObservationLease lease,
        ResourceDeploymentObservationContext context,
        long sourceControlVersion,
        string bindingId,
        ResourceDeploymentBoundObservation observation,
        CancellationToken ct = default)
    {
        Validate(lease, context);
        ValidateOpaque(bindingId);
        if (bindingId != observation.BindingId)
            return new ObservationStoreRejected(ObservationStoreRejection.BindingChanged);
        var stored = ObservationValueMappings.Store(observation.Observation);
        var diagnostics = ObservationValueMappings.Store(observation.Diagnostics);
        return (await core.CommitSnapshotAsync(Lease(lease),
                context,
                sourceControlVersion,
                bindingId,
                control =>
                    new ObservationSnapshotDocument(lease.DeploymentId,
                        lease.TenantId,
                        ObservationValueMappings.Pin(context),
                        bindingId,
                        sourceControlVersion,
                        control.ObservationVersion,
                        control.UpdatedAtUtc,
                        stored,
                        diagnostics,
                        ObservationValueMappings.Fingerprint(stored, diagnostics),
                        null),
                ct)) switch
        {
            MongoObservationSuccess<ObservationSnapshotDocument> { Value: var document } =>
                ObservationValueMappings.Load(document, context),
            MongoObservationRejected rejection => Map(rejection)
        };
    }

    public async Task<ObservationControlState?> LoadControlAsync(ObservationIdentity identity,
        CancellationToken ct = default)
    {
        ValidateObjectId(identity.TenantId);
        ValidateObjectId(identity.DeploymentId);
        var control = await core.LoadControlAsync(new(identity.DeploymentId, identity.TenantId), ct);
        if (control is null)
            return null;
        return new(control.Epoch,
            control.SourceControlVersion,
            control.ObservationVersion,
            control.SourceHealthy,
            control.BindingId,
            control.Token is null ? null : control.LeaseExpiresAtUtc);
    }

    public async Task<ResourceDeploymentSharedObservation?> LoadFreshSnapshotAsync(
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default)
    {
        Validate(context);
        var document = await core.LoadFreshAsync(context, ct);
        return document is null
            ? null
            : ObservationValueMappings.Load(document, context);
    }

    private static MongoObservationDemand Demand(ObservationDemandIdentity identity, DateTimeOffset expires) =>
        new(DemandIdentity(identity), expires);

    private static MongoObservationDemandIdentity DemandIdentity(ObservationDemandIdentity identity) =>
        new(new(identity.DeploymentId, identity.TenantId),
            identity.SubscriptionId,
            identity.ConnectionId,
            identity.SessionId);

    private static MongoObservationLease Lease(ObservationLease lease) => new(
        new(lease.DeploymentId, lease.TenantId),
        lease.InstanceId,
        lease.Token,
        lease.Epoch);

    private static ObservationMutationOutcome Mutation<T>(MongoObservationOutcome<T> outcome)
        where T : notnull => outcome switch
        {
            MongoObservationSuccess<T> => new ObservationMutationAccepted(),
            MongoObservationRejected rejection => Map(rejection)
        };

    private static ObservationStoreRejected Map(MongoObservationRejected rejection) => new(rejection.Reason switch
    {
        MongoObservationRejection.Busy => ObservationStoreRejection.Busy,
        MongoObservationRejection.NoDemand => ObservationStoreRejection.NoDemand,
        MongoObservationRejection.LeaseLost => ObservationStoreRejection.LeaseLost,
        MongoObservationRejection.Superseded => ObservationStoreRejection.Superseded,
        MongoObservationRejection.SourceChanged => ObservationStoreRejection.BindingChanged,
        MongoObservationRejection.TargetUnavailable => ObservationStoreRejection.TargetUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(rejection))
    });

    private sealed class Definition(
        IMongoDatabase database,
        IMongoCollection<ResourceDeployment> deployments,
        FenceDeploymentTargetObservation fenceTarget,
        StoreOutboxWrites storeOutboxWrites) : IMongoObservationDefinition<
            ResourceDeploymentObservationContext,
            ObservationControlDocument,
            ObservationDemandDocument,
            ObservationSnapshotDocument>
    {
        public MongoObservationScope Scope(ResourceDeploymentObservationContext context) =>
            new(context.Deployment.Id, context.Deployment.TenantId);

        public MongoObservationScope ScopeOf(ObservationControlDocument control) => new(control.Id, control.TenantId);
        public Expression<Func<ObservationDemandDocument, string>> DemandSubject => item => item.DeploymentId;
        public Expression<Func<ObservationControlDocument, string?>> Source => item => item.BindingId;
        public Expression<Func<ObservationSnapshotDocument, string>> SnapshotSource => item => item.BindingId;

        public FilterDefinition<ObservationControlDocument> ControlScope(MongoObservationScope scope) =>
            ObservationStoreFilters.ForControlScope(scope);

        public FilterDefinition<ObservationDemandDocument> DemandScope(MongoObservationScope scope) =>
            ObservationStoreFilters.ForDemandScope(scope);

        public FilterDefinition<ObservationSnapshotDocument> SnapshotScope(MongoObservationScope scope) =>
            ObservationStoreFilters.ForSnapshotScope(scope);

        public FilterDefinition<ObservationControlDocument> ControlPin(ResourceDeploymentObservationContext context) =>
            ObservationStoreFilters.ForControlContext(context);

        public UpdateDefinition<ObservationDemandDocument> DemandIdentityUpdate(MongoObservationScope scope) =>
            Builders<ObservationDemandDocument>.Update.Set(item => item.DeploymentId, scope.Id)
                .Set(item => item.TenantId, scope.TenantId!);

        public ObservationControlDocument CreateControl(ResourceDeploymentObservationContext context) => new(
            context.Deployment.Id,
            context.Deployment.TenantId,
            ObservationValueMappings.Pin(context),
            Epoch: 0,
            SourceControlVersion: 0,
            ObservationVersion: 0,
            DemandFence: 0,
            InstanceId: null,
            Token: null,
            LeaseExpiresAtUtc: DateTime.UnixEpoch,
            SourceHealthy: false,
            BindingId: null,
            IdleSinceUtc: null,
            UpdatedAtUtc: DateTime.UnixEpoch);

        public bool HasPin(ObservationControlDocument control, ResourceDeploymentObservationContext context) =>
            control.Pin == ObservationValueMappings.Pin(context);

        public UpdateDefinition<ObservationControlDocument> PinUpdate(ResourceDeploymentObservationContext context) =>
            Builders<ObservationControlDocument>.Update.Set(item => item.Pin, ObservationValueMappings.Pin(context));

        public FilterDefinition<ObservationSnapshotDocument> SnapshotPin(
            ResourceDeploymentObservationContext context) =>
            ObservationStoreFilters.ForSnapshotContext(context);
        public async Task<MongoObservationRejected?> FenceAsync(ResourceDeploymentObservationContext context,
            IClientSessionHandle session,
            CancellationToken ct)
        {
            var target =
                await fenceTarget(new(context.Deployment.Placement.DeploymentTargetId, context.TargetConnectionVersion),
                    session,
                    ct);
            if (target is DeploymentTargetObservationFenceRejected)
                return new(MongoObservationRejection.TargetUnavailable);
            var fenced = await deployments.UpdateOneAsync(session,
                ResourceDeploymentFilters.ForCurrentObservation(context),
                Builders<ResourceDeployment>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: ct);
            return fenced.ModifiedCount == 1 ? null : new(MongoObservationRejection.Superseded);
        }

        public Task PublishAsync(ObservationControlDocument control,
            IClientSessionHandle session,
            CancellationToken ct) =>
            storeOutboxWrites(session,
                [
                    OutboxWrite.Create(new ResourceDeploymentObservationChangedEvent(
                            control.Id,
                            control.Pin.ResourceId,
                            control.Pin.ResourceGroupId,
                            control.Pin.DeploymentRevision,
                            control.Pin.DesiredGeneration,
                            control.ObservationVersion)
                        { EventCore = EventCore.Create(control.TenantId) })
                ],
                ct);
    }

    private static void Validate(ResourceDeploymentObservationContext context)
    {
        ValidateObjectId(context.Deployment.TenantId);
        ValidateObjectId(context.Deployment.Id);
        if (context.DeploymentRevision < 1 || context.TargetConnectionVersion < 1 ||
            context.Deployment.DesiredGeneration < 1)
            throw new ArgumentException("Observation context versions must be positive.", nameof(context));
    }

    private static void Validate(ObservationLease lease)
    {
        ValidateObjectId(lease.TenantId);
        ValidateObjectId(lease.DeploymentId);
        ValidateOpaque(lease.InstanceId);
        ValidateOpaque(lease.Token);
        if (lease.Epoch < 1)
            throw new ArgumentException("An observation lease epoch must be positive.", nameof(lease));
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
        if (!ObjectId.TryParse(value, out var id) || id.ToString() != value)
            throw new ArgumentException("A canonical Mongo identity is required.");
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
