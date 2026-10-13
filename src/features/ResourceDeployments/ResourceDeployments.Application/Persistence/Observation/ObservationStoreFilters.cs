using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Observation;

internal static class ObservationStoreFilters
{
    internal static FilterDefinition<ObservationDemandDocument> DemandByKey(string key) =>
        Builders<ObservationDemandDocument>.Filter.Eq(item => item.Id, key);
    internal static FilterDefinition<ObservationControlDocument> NonIdleControls() =>
        Builders<ObservationControlDocument>.Filter.Eq(item => item.IdleSinceUtc, null);
    internal static string DemandKey(ObservationDemandIdentity identity) => $"{identity.TenantId}/{identity.DeploymentId}/{identity.SubscriptionId}";
    internal static FilterDefinition<ObservationControlDocument> ControlIdentity(string tenant, string deployment) =>
        Builders<ObservationControlDocument>.Filter.Eq(item => item.Id, deployment) & Builders<ObservationControlDocument>.Filter.Eq(item => item.TenantId, tenant);
    internal static FilterDefinition<ObservationSnapshotDocument> SnapshotIdentity(string tenant, string deployment) =>
        Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.Id, deployment) & Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.TenantId, tenant);
    internal static FilterDefinition<ObservationDemandDocument> DemandScope(string tenant, string deployment) =>
        Builders<ObservationDemandDocument>.Filter.Eq(item => item.TenantId, tenant) & Builders<ObservationDemandDocument>.Filter.Eq(item => item.DeploymentId, deployment);
    internal static FilterDefinition<ObservationDemandDocument> DemandIdentity(ObservationDemandIdentity identity) =>
        DemandScope(identity.TenantId, identity.DeploymentId) & Builders<ObservationDemandDocument>.Filter.Eq(item => item.Id, DemandKey(identity)) &
        Builders<ObservationDemandDocument>.Filter.Eq(item => item.ConnectionId, identity.ConnectionId) & Builders<ObservationDemandDocument>.Filter.Eq(item => item.SessionId, identity.SessionId);
    internal static FilterDefinition<ObservationControlDocument> LeaseIdentity(ObservationLease lease) => ControlIdentity(lease.TenantId, lease.DeploymentId) &
        Builders<ObservationControlDocument>.Filter.Eq(item => item.InstanceId, lease.InstanceId) & Builders<ObservationControlDocument>.Filter.Eq(item => item.Token, lease.Token) &
        Builders<ObservationControlDocument>.Filter.Eq(item => item.Epoch, lease.Epoch);
    internal static FilterDefinition<ObservationControlDocument> ActiveLease(ObservationLease lease) => LeaseIdentity(lease) & ServerControlExpiry("$gt");
    internal static FilterDefinition<ObservationControlDocument> PinFilter(ResourceDeploymentObservationContext context) =>
        Builders<ObservationControlDocument>.Filter.Eq(item => item.Pin, ObservationValueMappings.Pin(context));
    internal static FilterDefinition<ObservationControlDocument> ServerControlExpiry(string comparison) => new BsonDocument("$expr", new BsonDocument(comparison, new BsonArray { "$LeaseExpiresAtUtc", "$$NOW" }));
    internal static FilterDefinition<ObservationDemandDocument> ServerExpiry(string field, string comparison) => new BsonDocument("$expr", new BsonDocument(comparison, new BsonArray { "$" + field, "$$NOW" }));
    internal static FilterDefinition<ObservationDemandDocument> ActiveDemandsAfter(string? after) => after is null
        ? ServerExpiry("ExpiresAtUtc", "$gt")
        : ServerExpiry("ExpiresAtUtc", "$gt") & Builders<ObservationDemandDocument>.Filter.Gt(item => item.DeploymentId, after);
    internal static FilterDefinition<ObservationControlDocument> ActiveOwner(string tenant, string deployment) =>
        ControlIdentity(tenant, deployment) & Builders<ObservationControlDocument>.Filter.Ne(item => item.Token, null) & ServerControlExpiry("$gt");
    internal static FilterDefinition<ObservationControlDocument> ControlAtEpoch(ObservationControlDocument control) =>
        ControlIdentity(control.TenantId, control.Id) & Builders<ObservationControlDocument>.Filter.Eq(item => item.Epoch, control.Epoch);
    internal static FilterDefinition<ObservationControlDocument> SourceAtVersion(ObservationLease lease, ResourceDeploymentObservationContext context, long version) =>
        ActiveLease(lease) & PinFilter(context) & Builders<ObservationControlDocument>.Filter.Eq(item => item.SourceControlVersion, version);
    internal static FilterDefinition<ObservationControlDocument> SnapshotCommit(ObservationLease lease, ResourceDeploymentObservationContext context,
        long sourceVersion, string bindingId, long observationVersion) =>
        SourceAtVersion(lease, context, sourceVersion) & Builders<ObservationControlDocument>.Filter.Eq(item => item.SourceHealthy, true) &
        Builders<ObservationControlDocument>.Filter.Eq(item => item.BindingId, bindingId) & Builders<ObservationControlDocument>.Filter.Eq(item => item.ObservationVersion, observationVersion);
    internal static FilterDefinition<ObservationControlDocument> HealthyOwner(ResourceDeploymentObservationContext context) =>
        ActiveOwner(context.Deployment.TenantId, context.Deployment.Id) & PinFilter(context) & Builders<ObservationControlDocument>.Filter.Eq(item => item.SourceHealthy, true);
    internal static FilterDefinition<ObservationSnapshotDocument> CurrentFreshSnapshot(ObservationControlDocument control) =>
        SnapshotIdentity(control.TenantId, control.Id) & Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.SourceControlVersion, control.SourceControlVersion) &
        Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.ObservationVersion, control.ObservationVersion) &
        Builders<ObservationSnapshotDocument>.Filter.Eq(item => item.BindingId, control.BindingId) &
        new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
        {
            new BsonDocument("$gt", new BsonArray { "$CheckedAtUtc", ServerAfter(-45) }),
            new BsonDocument("$lte", new BsonArray { "$CheckedAtUtc", "$$NOW" })
        }));
    internal static FilterDefinition<ObservationControlDocument> NonIdleControl(ObservationControlDocument control) =>
        ControlIdentity(control.TenantId, control.Id) & NonIdleControls();
    internal static FilterDefinition<ObservationControlDocument> CommitDeadline(ObservationLease lease, ResourceDeploymentObservationContext context,
        ObservationControlDocument expected) => SourceAtVersion(lease, context, expected.SourceControlVersion) &
        Builders<ObservationControlDocument>.Filter.Eq(item => item.ObservationVersion, expected.ObservationVersion);
    internal static BsonDocument ServerAfter(int seconds) => new("$dateAdd", new BsonDocument { { "startDate", "$$NOW" }, { "unit", "second" }, { "amount", seconds } });
}
