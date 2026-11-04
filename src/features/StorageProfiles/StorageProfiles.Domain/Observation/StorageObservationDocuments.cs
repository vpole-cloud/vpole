using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Observation;

namespace Pole.Features.StorageProfiles.Domain.Observation;

public sealed record StorageObservationControlDocument(
    [property: BsonId, BsonRepresentation(BsonType.ObjectId)]
    string Id,
    long ConnectionVersion,
    long Epoch,
    long SourceControlVersion,
    long ObservationVersion,
    long DemandFence,
    string? InstanceId,
    string? Token,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime LeaseExpiresAtUtc,
    bool SourceHealthy,
    string? SourceId,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime? IdleSinceUtc,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime UpdatedAtUtc) : IObservationControl;

public sealed record StorageObservationDemandDocument(
    [property: BsonId] string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string DeploymentTargetId,
    string SubscriptionId,
    string ConnectionId,
    string SessionId,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime OriginalExpiresAtUtc,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime ExpiresAtUtc) : IObservationDemand;

// Only closed concrete metadata arrays are serialized; no Kubernetes model is stored.
public sealed record StorageObservationSnapshotDocument(
    [property: BsonId, BsonRepresentation(BsonType.ObjectId)]
    string Id,
    long ConnectionVersion,
    string SourceId,
    long SourceControlVersion,
    long ObservationVersion,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime CheckedAtUtc,
    StoredStorageInventory Inventory,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime? ExpiresAtUtc) : IObservationSnapshot;
