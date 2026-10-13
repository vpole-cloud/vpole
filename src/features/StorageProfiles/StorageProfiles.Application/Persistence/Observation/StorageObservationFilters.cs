using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Persistence.Observation;

internal static class StorageObservationFilters
{
    internal static FilterDefinition<StorageObservationControlDocument> Control(string target) => Builders<StorageObservationControlDocument>.Filter.Eq(item => item.Id, target);
    internal static FilterDefinition<StorageObservationControlDocument> NonIdle() => Builders<StorageObservationControlDocument>.Filter.Eq(item => item.IdleSinceUtc, null);
    internal static FilterDefinition<StorageObservationControlDocument> AtEpoch(StorageObservationControlDocument control) => Control(control.Id) & Builders<StorageObservationControlDocument>.Filter.Eq(item => item.Epoch, control.Epoch);
    internal static FilterDefinition<StorageObservationControlDocument> CurrentContext(StorageObservationContext context) => Control(context.DeploymentTargetId) & Builders<StorageObservationControlDocument>.Filter.Eq(item => item.ConnectionVersion, context.ConnectionVersion);
    internal static FilterDefinition<StorageObservationControlDocument> LeaseIdentity(StorageObservationLease lease) => Control(lease.DeploymentTargetId) &
        Builders<StorageObservationControlDocument>.Filter.Eq(item => item.InstanceId, lease.InstanceId) & Builders<StorageObservationControlDocument>.Filter.Eq(item => item.Token, lease.Token) & Builders<StorageObservationControlDocument>.Filter.Eq(item => item.Epoch, lease.Epoch);
    internal static FilterDefinition<StorageObservationControlDocument> ActiveLease(StorageObservationLease lease) => LeaseIdentity(lease) & ServerLeaseExpiry();
    internal static FilterDefinition<StorageObservationControlDocument> ActiveOwner(string target) => Control(target) & Builders<StorageObservationControlDocument>.Filter.Ne(item => item.Token, null) & ServerLeaseExpiry();
    internal static FilterDefinition<StorageObservationControlDocument> SourceVersion(StorageObservationLease lease, StorageObservationContext context, long version) => ActiveLease(lease) & CurrentContext(context) & Builders<StorageObservationControlDocument>.Filter.Eq(item => item.SourceControlVersion, version);
    internal static FilterDefinition<StorageObservationControlDocument> Commit(StorageObservationLease lease, StorageObservationContext context, long sourceVersion, string sourceId, long observationVersion) =>
        SourceVersion(lease, context, sourceVersion) & Builders<StorageObservationControlDocument>.Filter.Eq(item => item.SourceHealthy, true) &
        Builders<StorageObservationControlDocument>.Filter.Eq(item => item.SourceId, sourceId) & Builders<StorageObservationControlDocument>.Filter.Eq(item => item.ObservationVersion, observationVersion);
    internal static FilterDefinition<StorageObservationControlDocument> CommitDeadline(StorageObservationLease lease, StorageObservationContext context, StorageObservationControlDocument control) =>
        SourceVersion(lease, context, control.SourceControlVersion) & Builders<StorageObservationControlDocument>.Filter.Eq(item => item.ObservationVersion, control.ObservationVersion);
    internal static FilterDefinition<StorageObservationControlDocument> HealthyOwner(StorageObservationContext context) => CurrentContext(context) & ActiveOwner(context.DeploymentTargetId) & Builders<StorageObservationControlDocument>.Filter.Eq(item => item.SourceHealthy, true);
    private static FilterDefinition<StorageObservationControlDocument> ServerLeaseExpiry() => new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$LeaseExpiresAtUtc", "$$NOW" }));
    internal static string DemandKey(StorageObservationDemandIdentity identity) => identity.DeploymentTargetId + "/" + identity.SubscriptionId;
    internal static FilterDefinition<StorageObservationDemandDocument> DemandByKey(string key) => Builders<StorageObservationDemandDocument>.Filter.Eq(item => item.Id, key);
    internal static FilterDefinition<StorageObservationDemandDocument> DemandIdentity(StorageObservationDemandIdentity identity) => DemandByKey(DemandKey(identity)) &
        Builders<StorageObservationDemandDocument>.Filter.Eq(item => item.ConnectionId, identity.ConnectionId) & Builders<StorageObservationDemandDocument>.Filter.Eq(item => item.SessionId, identity.SessionId);
    internal static FilterDefinition<StorageObservationDemandDocument> DemandScope(string target) => Builders<StorageObservationDemandDocument>.Filter.Eq(item => item.DeploymentTargetId, target);
    internal static FilterDefinition<StorageObservationDemandDocument> DemandExpiry(string comparison) => new BsonDocument("$expr", new BsonDocument(comparison, new BsonArray { "$ExpiresAtUtc", "$$NOW" }));
    internal static FilterDefinition<StorageObservationDemandDocument> DemandsAfter(string? after) => after is null ? DemandExpiry("$gt") : DemandExpiry("$gt") & Builders<StorageObservationDemandDocument>.Filter.Gt(item => item.DeploymentTargetId, after);
    internal static FilterDefinition<StorageObservationSnapshotDocument> Snapshot(string target) => Builders<StorageObservationSnapshotDocument>.Filter.Eq(item => item.Id, target);
    internal static FilterDefinition<StorageObservationSnapshotDocument> FreshSnapshot(StorageObservationControlDocument control) => Snapshot(control.Id) &
        Builders<StorageObservationSnapshotDocument>.Filter.Eq(item => item.ConnectionVersion, control.ConnectionVersion) & Builders<StorageObservationSnapshotDocument>.Filter.Eq(item => item.SourceId, control.SourceId) &
        Builders<StorageObservationSnapshotDocument>.Filter.Eq(item => item.SourceControlVersion, control.SourceControlVersion) & Builders<StorageObservationSnapshotDocument>.Filter.Eq(item => item.ObservationVersion, control.ObservationVersion) &
        new BsonDocument("$expr", new BsonDocument("$and", new BsonArray { new BsonDocument("$gt", new BsonArray { "$CheckedAtUtc", ServerAfter(-45) }), new BsonDocument("$lte", new BsonArray { "$CheckedAtUtc", "$$NOW" }) }));
    internal static BsonDocument ServerAfter(int seconds) => new("$dateAdd", new BsonDocument { { "startDate", "$$NOW" }, { "unit", "second" }, { "amount", seconds } });
}
