using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Observation;

namespace Pole.Features.ResourceDeployments.Domain.Observation;

// Concrete BSON shapes deliberately do not persist the JSON-polymorphic Workloads contracts.
public sealed record ObservationContextPin(
    string ResourceType,
    string ResourceId,
    string ResourceGroupId,
    string IsolationScopeId,
    string RuntimeCellId,
    long ResourceRevision,
    long DeploymentRevision,
    long DesiredGeneration,
    string DeploymentTargetId,
    long TargetConnectionVersion,
    long TargetWorkloadConfigurationRevision,
    string KubernetesNamespace);

public sealed record ObservationControlDocument(
    [property: BsonId, BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    ObservationContextPin Pin,
    long Epoch,
    long SourceControlVersion,
    long ObservationVersion,
    long DemandFence,
    string? InstanceId,
    string? Token,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime LeaseExpiresAtUtc,
    bool SourceHealthy,
    string? BindingId,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime? IdleSinceUtc,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime UpdatedAtUtc) : IObservationControl;

public sealed record ObservationDemandDocument(
    [property: BsonId] string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string DeploymentId,
    string SubscriptionId,
    string ConnectionId,
    string SessionId,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime OriginalExpiresAtUtc,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime ExpiresAtUtc) : IObservationDemand;

public sealed record ObservationSnapshotDocument(
    [property: BsonId, BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    ObservationContextPin Pin,
    string BindingId,
    long SourceControlVersion,
    long ObservationVersion,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime CheckedAtUtc,
    StoredObservation Observation,
    StoredDiagnostics? Diagnostics,
    string Fingerprint,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime? ExpiresAtUtc) : IObservationSnapshot;

public sealed record StoredObservation(
    string Kind,
    string? Reason,
    StoredObservedWorkload? Observed);

public sealed record StoredObservedWorkload(
    bool Ready,
    bool RolloutCurrent,
    int DesiredReplicas,
    int CurrentReplicas,
    int UpdatedReplicas,
    int ReadyReplicas,
    int AvailableReplicas,
    bool PodsTruncated,
    StoredPod[] Pods,
    StoredEvent[] Events,
    string? EventsUnavailableReason);

public sealed record StoredPod(
    string Name,
    string Phase,
    bool Ready,
    bool CurrentGeneration,
    int RestartCount,
    string[] Reasons);

public sealed record StoredEvent(
    string Reason,
    int Count,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime? LastSeenAtUtc);

public sealed record StoredDiagnostics(
    string Kind,
    string? Reason,
    StoredAvailableDiagnostics? Available);

public sealed record StoredAvailableDiagnostics(
    StoredInstance[] Instances,
    bool InstancesTruncated,
    StoredDiagnosticEvent[] Events,
    bool EventsTruncated,
    string? EventsUnavailableReason);

public sealed record StoredInstance(
    string Uid,
    string Name,
    string Phase,
    bool Ready,
    bool CurrentGeneration,
    int RestartCount,
    string[] Reasons,
    StoredContainer[] Containers,
    bool ContainersTruncated);

public sealed record StoredContainer(
    string Name,
    string Kind,
    bool PreviousAvailable);

public sealed record StoredDiagnosticEvent(
    string ObjectKind,
    string ObjectName,
    string Reason,
    int Count,
    [property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime? LastSeenAtUtc);
