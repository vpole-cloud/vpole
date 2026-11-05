using Pole.Architecture;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Observation;
using static Pole.Database.Observation.MongoObservationExpressions;

namespace Pole.Database.Observation;

// Each control transition remains ONE server-side $set stage, including the clock and fences.
[ViolateArchitecture(ArchitectureRules.RawBson,
    "Atomic observation transitions combine server timestamps, original expiry bounds and fences in " +
    "one computed $set pipeline.")]
internal sealed class MongoObservationPipelines<TContext, TControl, TDemand, TSnapshot>(
    IMongoObservationDefinition<TContext, TControl, TDemand, TSnapshot> definition)
    where TControl : class, IObservationControl
    where TDemand : class, IObservationDemand
    where TSnapshot : class, IObservationSnapshot
{
    private static UpdateDefinitionBuilder<TControl> Control => Builders<TControl>.Update;

    internal UpdateDefinition<TDemand> RenewDemand(MongoObservationDemand demand)
    {
        var assignments = definition.DemandIdentityUpdate(demand.Identity.Scope)
            .Set(item => item.SubscriptionId, demand.Identity.SubscriptionId)
            .Set(item => item.ConnectionId, demand.Identity.ConnectionId)
            .Set(item => item.SessionId, demand.Identity.SessionId);
        var fields = LiteralAssignments(assignments);
        var bound = new BsonDocument("$min", new BsonArray
        {
            new BsonDocument("$ifNull", new BsonArray
            {
                Reference<TDemand, DateTime>(item => item.OriginalExpiresAtUtc),
                Literal(new BsonDateTime(demand.ExpiresAtUtc.UtcDateTime))
            }),
            Literal(new BsonDateTime(demand.ExpiresAtUtc.UtcDateTime))
        });
        fields.Add(Field<TDemand, DateTime>(item => item.OriginalExpiresAtUtc), bound);
        fields.Add(Field<TDemand, DateTime>(item => item.ExpiresAtUtc),
            new BsonDocument("$min", new BsonArray { bound, After(ObservationPolicy.DemandDuration) }));
        return Set<TDemand>(fields);
    }

    internal UpdateDefinition<TControl> ActivateDemand(TContext context, bool contextChanged)
    {
        var fields = LiteralAssignments(Control.Set(item => item.IdleSinceUtc, null));
        if (contextChanged)
        {
            Invalidate(fields);
            AdvanceObservation(fields);
            fields.AddRange(LiteralAssignments(definition.PinUpdate(context)));
        }
        return Transition(fields);
    }

    internal UpdateDefinition<TControl> Claim(TContext context, string instanceId, bool changed)
    {
        var fields = LiteralAssignments(Control
            .Set(item => item.InstanceId, instanceId)
            .Set(item => item.Token, Guid.NewGuid().ToString("N"))
            .Set(item => item.SourceHealthy, false)
            .Set(definition.Source, null)
            .Set(item => item.IdleSinceUtc, null));
        fields.Add(Field<TControl, long>(item => item.Epoch), Increment<TControl>(item => item.Epoch));
        fields.Add(Field<TControl, long>(item => item.SourceControlVersion),
            Increment<TControl>(item => item.SourceControlVersion));
        fields.Add(Field<TControl, DateTime>(item => item.LeaseExpiresAtUtc), After(ObservationPolicy.LeaseDuration));
        fields.AddRange(LiteralAssignments(definition.PinUpdate(context)));
        if (changed)
            AdvanceObservation(fields);
        return Transition(fields);
    }

    internal UpdateDefinition<TControl> RenewLease() => Transition(new BsonDocument(
        Field<TControl, DateTime>(item => item.LeaseExpiresAtUtc), After(ObservationPolicy.LeaseDuration)));

    internal UpdateDefinition<TControl> Release()
    {
        var fields = new BsonDocument();
        AdvanceObservation(fields);
        Invalidate(fields);
        return Transition(fields);
    }

    internal UpdateDefinition<TControl> SetSource(string? sourceId)
    {
        var fields = LiteralAssignments(Control.Set(item => item.SourceHealthy, sourceId is not null)
            .Set(definition.Source, sourceId));
        fields.Add(Field<TControl, long>(item => item.SourceControlVersion),
            Increment<TControl>(item => item.SourceControlVersion));
        AdvanceObservation(fields);
        return Transition(fields);
    }

    internal UpdateDefinition<TControl> CommitSnapshot()
    {
        var fields = new BsonDocument();
        AdvanceObservation(fields);
        return Transition(fields);
    }

    internal UpdateDefinition<TControl> Touch() => Transition(new BsonDocument());

    internal UpdateDefinition<TControl> MarkIdle()
    {
        var fields = new BsonDocument(Field<TControl, DateTime?>(item => item.IdleSinceUtc), "$$NOW");
        AdvanceObservation(fields);
        Invalidate(fields);
        return Transition(fields);
    }

    internal UpdateDefinition<TSnapshot> ClearSnapshotExpiry() =>
        Builders<TSnapshot>.Update.Set(item => item.ExpiresAtUtc, null);

    internal UpdateDefinition<TSnapshot> RetainIdleSnapshot() => Set<TSnapshot>(new BsonDocument(
        Field<TSnapshot, DateTime?>(item => item.ExpiresAtUtc),
        new BsonDocument("$ifNull", new BsonArray
        {
            Reference<TSnapshot, DateTime?>(item => item.ExpiresAtUtc), After(ObservationPolicy.IdleRetention)
        })));

    private static UpdateDefinition<T> Set<T>(BsonDocument fields) =>
        Builders<T>.Update.Pipeline(new[] { new BsonDocument("$set", fields) });

    private static UpdateDefinition<TControl> Transition(BsonDocument fields)
    {
        fields.Add(Field<TControl, long>(item => item.DemandFence), Increment<TControl>(item => item.DemandFence));
        fields.Add(Field<TControl, DateTime>(item => item.UpdatedAtUtc), "$$NOW");
        return Set<TControl>(fields);
    }

    private static void AdvanceObservation(BsonDocument fields) =>
        fields.Add(Field<TControl, long>(item => item.ObservationVersion),
            Increment<TControl>(item => item.ObservationVersion));

    private void Invalidate(BsonDocument fields)
    {
        fields.Add(Field<TControl, long>(item => item.Epoch), Increment<TControl>(item => item.Epoch));
        fields.Add(Field<TControl, long>(item => item.SourceControlVersion),
            Increment<TControl>(item => item.SourceControlVersion));
        fields.AddRange(LiteralAssignments(Control
            .Set(item => item.InstanceId, null)
            .Set(item => item.Token, null)
            .Set(item => item.LeaseExpiresAtUtc, DateTime.UnixEpoch)
            .Set(item => item.SourceHealthy, false)
            .Set(definition.Source, null)));
    }
}
