using MongoDB.Driver;
using Pole.Observation;

namespace Pole.Database.Observation;

internal sealed class MongoObservationFilters<TContext, TControl, TDemand, TSnapshot>(
    IMongoObservationDefinition<TContext, TControl, TDemand, TSnapshot> definition
)
    where TControl : class, IObservationControl
    where TDemand : class, IObservationDemand
    where TSnapshot : class, IObservationSnapshot
{
    private static FilterDefinitionBuilder<TControl> Control => Builders<TControl>.Filter;
    private static FilterDefinitionBuilder<TDemand> Demand => Builders<TDemand>.Filter;
    private static FilterDefinitionBuilder<TSnapshot> Snapshot => Builders<TSnapshot>.Filter;

    internal FilterDefinition<TControl> ControlScope(MongoObservationScope scope) => definition.ControlScope(scope);
    internal FilterDefinition<TSnapshot> SnapshotScope(MongoObservationScope scope) => definition.SnapshotScope(scope);

    internal FilterDefinition<TDemand> DemandKey(MongoObservationDemandIdentity identity) => Demand.Eq(
        item => item.Id,
        identity.Scope.DemandKey(identity.SubscriptionId));

    internal FilterDefinition<TDemand> ExactDemand(MongoObservationDemandIdentity identity) => Demand.And(
        DemandKey(identity),
        definition.DemandScope(identity.Scope),
        Demand.Eq(item => item.ConnectionId, identity.ConnectionId),
        Demand.Eq(item => item.SessionId, identity.SessionId));

    internal FilterDefinition<TDemand> LiveDemandKey(MongoObservationDemandIdentity identity) => DemandKey(identity) &
        MongoObservationExpressions.Future<TDemand>(item => item.ExpiresAtUtc);

    internal FilterDefinition<TDemand> ActiveDemand(MongoObservationScope scope) => definition.DemandScope(scope) &
        MongoObservationExpressions.Future<TDemand>(item => item.ExpiresAtUtc);

    internal FilterDefinition<TDemand> ExpiredDemand(MongoObservationScope scope) => definition.DemandScope(scope) &
        MongoObservationExpressions.Expired<TDemand>(item => item.ExpiresAtUtc);

    internal FilterDefinition<TDemand> DemandPage(string? after)
    {
        var active = MongoObservationExpressions.Future<TDemand>(item => item.ExpiresAtUtc);
        return after is null ? active : active & Demand.Gt(definition.DemandSubject, after);
    }

    internal FilterDefinition<TControl> ActiveControls() => Control.Eq(item => item.IdleSinceUtc, null);

    internal FilterDefinition<TControl> NotIdle(MongoObservationScope scope) => ControlScope(scope) & ActiveControls();

    internal FilterDefinition<TControl> Epoch(MongoObservationScope scope, long epoch) =>
        ControlScope(scope) & Control.Eq(item => item.Epoch, epoch);

    internal FilterDefinition<TControl> LeaseIdentity(MongoObservationLease lease) => Control.And(
        ControlScope(lease.Scope),
        Control.Eq(item => item.InstanceId, lease.InstanceId),
        Control.Eq(item => item.Token, lease.Token),
        Control.Eq(item => item.Epoch, lease.Epoch));

    internal FilterDefinition<TControl> BusyLease(MongoObservationScope scope) => Control.And(
        ControlScope(scope),
        Control.Ne(item => item.Token, null),
        MongoObservationExpressions.Future<TControl>(item => item.LeaseExpiresAtUtc));

    internal FilterDefinition<TControl> ActiveLease(MongoObservationLease lease, TContext context) => Control.And(
        LeaseIdentity(lease),
        definition.ControlPin(context),
        MongoObservationExpressions.Future<TControl>(item => item.LeaseExpiresAtUtc));

    internal FilterDefinition<TControl> SourceVersion(
        MongoObservationLease lease,
        TContext context,
        long version) => ActiveLease(lease, context) & Control.Eq(item => item.SourceControlVersion, version);

    internal FilterDefinition<TControl> SnapshotCommit(
        MongoObservationLease lease,
        TContext context,
        long sourceVersion,
        string sourceId,
        long observationVersion) => Control.And(
        SourceVersion(lease, context, sourceVersion),
        Control.Eq(definition.Source, sourceId),
        Control.Eq(item => item.SourceHealthy, true),
        Control.Eq(item => item.ObservationVersion, observationVersion));

    internal FilterDefinition<TControl> HealthyLease(TContext context) => Control.And(
        BusyLease(definition.Scope(context)),
        definition.ControlPin(context),
        Control.Eq(item => item.SourceHealthy, true));

    internal FilterDefinition<TSnapshot> FreshSnapshot(
        TContext context,
        TControl control,
        string? sourceId) => Snapshot.And(
        SnapshotScope(definition.Scope(context)),
        definition.SnapshotPin(context),
        Snapshot.Eq(item => item.SourceControlVersion, control.SourceControlVersion),
        Snapshot.Eq(item => item.ObservationVersion, control.ObservationVersion),
        Snapshot.Eq(definition.SnapshotSource, sourceId!),
        MongoObservationExpressions.Fresh<TSnapshot>(item => item.CheckedAtUtc));

    internal FilterDefinition<TControl> Deadline(
        MongoObservationLease lease,
        TContext context,
        TControl expected) => Control.And(
        ActiveLease(lease, context),
        Control.Eq(item => item.SourceControlVersion, expected.SourceControlVersion),
        Control.Eq(item => item.ObservationVersion, expected.ObservationVersion));
}
