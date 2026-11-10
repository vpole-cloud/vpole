using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Observation;

public sealed record ObservationIdentity(
    string TenantId,
    string DeploymentId
);

public sealed record ObservationDemandIdentity(
    string TenantId,
    string DeploymentId,
    string SubscriptionId,
    string ConnectionId,
    string SessionId
);

public sealed record ObservationDemand(
    ObservationDemandIdentity Identity,
    DateTimeOffset ExpiresAtUtc
);

public sealed record ObservationLease(
    string TenantId,
    string DeploymentId,
    string InstanceId,
    string Token,
    long Epoch
);

public sealed record ObservationOwnership(
    ObservationLease Lease,
    long SourceControlVersion
);

public sealed record ObservationSourceState(
    long SourceControlVersion
);

public sealed record ObservationMutationAccepted;

public enum ObservationStoreRejection
{
    Busy,
    NoDemand,
    LeaseLost,
    Superseded,
    BindingChanged,
    TargetUnavailable,
}

public sealed record ObservationStoreRejected(
    ObservationStoreRejection Reason
);

public union ObservationMutationOutcome(ObservationMutationAccepted, ObservationStoreRejected);

public union ObservationLeaseOutcome(ObservationOwnership, ObservationStoreRejected);

public union ObservationSourceStateOutcome(ObservationSourceState, ObservationStoreRejected);

public union ObservationSnapshotOutcome(ResourceDeploymentSharedObservation, ObservationStoreRejected);

/// <summary>Retained control counters survive owner loss and snapshot TTL cleanup.</summary>
public sealed record ObservationControlState(
    long Epoch,
    long SourceControlVersion,
    long ObservationVersion,
    bool SourceHealthy,
    string? BindingId,
    DateTime? LeaseExpiresAtUtc
);

public sealed record ResourceDeploymentSharedObservation(
    ResourceDeploymentObservationContext Context,
    string BindingId,
    long SourceControlVersion,
    long ObservationVersion,
    DateTime CheckedAtUtc,
    WorkloadObservation Observation,
    WorkloadDiagnostics? Diagnostics
);

public sealed record ObservationDemandPage(
    IReadOnlyList<ObservationIdentity> Items,
    string? NextCursor
);

public interface IResourceDeploymentObservationStore
{
    Task<ObservationMutationOutcome> PutDemandAsync(
        ObservationDemand demand,
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default);

    Task<ObservationMutationOutcome> RenewDemandAsync(
        ObservationDemand demand,
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default);

    Task<ObservationMutationOutcome> RemoveDemandAsync(
        ObservationDemandIdentity identity,
        CancellationToken ct = default);

    Task<ObservationDemandPage> ScanDemandsAsync(
        string? after,
        int limit,
        CancellationToken ct = default);

    Task ReapExpiredDemandsAsync(int limit, CancellationToken ct = default);

    Task<ObservationLeaseOutcome> ClaimLeaseAsync(
        ResourceDeploymentObservationContext context,
        string instanceId,
        CancellationToken ct = default);

    Task<ObservationMutationOutcome> RenewLeaseAsync(
        ObservationLease lease,
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default);

    Task<ObservationMutationOutcome> ReleaseLeaseAsync(ObservationLease lease, CancellationToken ct = default);

    Task<ObservationSourceStateOutcome> SetSourceStateAsync(
        ObservationLease lease,
        ResourceDeploymentObservationContext context,
        long expectedSourceControlVersion,
        bool healthy,
        string? bindingId,
        CancellationToken ct = default);

    Task<ObservationSnapshotOutcome> TryCommitSnapshotAsync(
        ObservationLease lease,
        ResourceDeploymentObservationContext context,
        long sourceControlVersion,
        string bindingId,
        ResourceDeploymentBoundObservation observation,
        CancellationToken ct = default);

    Task<ObservationControlState?> LoadControlAsync(ObservationIdentity identity, CancellationToken ct = default);

    Task<ResourceDeploymentSharedObservation?> LoadFreshSnapshotAsync(
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default);
}
