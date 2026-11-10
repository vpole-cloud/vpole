using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.Secrets.Contracts;

public sealed record ConsumerSecretPlanningRequest(
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    ResourceTypeId ConsumerType,
    string ConsumerId,
    IReadOnlyCollection<ConsumerSecretKeyReference> PreviousReferences,
    IReadOnlyCollection<ConsumerSecretKeyReference> DesiredReferences,
    IReadOnlyCollection<string> DeploymentIds
);

public sealed record SecretPlanningVersion(
    string SecretId,
    long Revision,
    long ReferenceFence
);

public sealed record ConsumerSecretPlanningSnapshot(
    ConsumerSecretPlanningRequest Request,
    IReadOnlyList<SecretPlanningVersion> Secrets
);

public sealed record ConsumerSecretPlanningFenced;

public sealed record ConsumerSecretPlanningChanged;

public union LoadConsumerSecretPlanningSnapshotOutcome(
    ConsumerSecretPlanningSnapshot,
    ConsumerSecretReferenceUnavailable);

public union FenceConsumerSecretPlanningSnapshotOutcome(
    ConsumerSecretPlanningFenced,
    ConsumerSecretPlanningChanged,
    ConsumerSecretReferenceUnavailable);

public delegate Task<LoadConsumerSecretPlanningSnapshotOutcome> LoadConsumerSecretPlanningSnapshot(
    ConsumerSecretPlanningRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

// Rechecks authority and takes technical write fences. It never modifies required or retained key pins.
public delegate Task<FenceConsumerSecretPlanningSnapshotOutcome> FenceConsumerSecretPlanningSnapshot(
    ConsumerSecretPlanningSnapshot request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
