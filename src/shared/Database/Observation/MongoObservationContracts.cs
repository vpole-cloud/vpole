using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Database.Observation;

public sealed record MongoObservationScope(
    string Id,
    string? TenantId,
    string DemandSubjectField)
{
    public BsonDocument Control => TenantId is null
        ? new("_id", ObjectId.Parse(Id))
        : new() { { "_id", ObjectId.Parse(Id) }, { "TenantId", ObjectId.Parse(TenantId) } };

    public BsonDocument Demands => TenantId is null
        ? new(DemandSubjectField, ObjectId.Parse(Id))
        : new() { { DemandSubjectField, ObjectId.Parse(Id) }, { "TenantId", ObjectId.Parse(TenantId) } };

    public string DemandKey(string subscription) =>
        TenantId is null ? $"{Id}/{subscription}" : $"{TenantId}/{Id}/{subscription}";
}

public sealed record MongoObservationLease(
    MongoObservationScope Scope,
    string InstanceId,
    string Token,
    long Epoch);

public sealed record MongoObservationDemandIdentity(
    MongoObservationScope Scope,
    string SubscriptionId,
    string ConnectionId,
    string SessionId);

public sealed record MongoObservationDemand(
    MongoObservationDemandIdentity Identity,
    DateTimeOffset ExpiresAtUtc);

public sealed record MongoObservationPage(
    IReadOnlyList<MongoObservationScope> Items,
    string? NextCursor);

public enum MongoObservationRejection
{
    Busy,
    NoDemand,
    LeaseLost,
    Superseded,
    SourceChanged,
    TargetUnavailable
}

public sealed record MongoObservationRejected(MongoObservationRejection Reason);

public union MongoObservationOutcome(
    BsonDocument,
    MongoObservationRejected);

/// <summary>Feature-owned context fencing and event creation participate in the SAME Mongo transaction.</summary>
public interface IMongoObservationDefinition<TContext>
{
    MongoObservationScope Scope(TContext context);
    BsonDocument Pin(TContext context);
    Task<MongoObservationRejected?> FenceAsync(TContext context, IClientSessionHandle session, CancellationToken ct);
    Task PublishAsync(BsonDocument control, IClientSessionHandle session, CancellationToken ct);
    bool AcceptSnapshot(BsonDocument document) => true;
}
