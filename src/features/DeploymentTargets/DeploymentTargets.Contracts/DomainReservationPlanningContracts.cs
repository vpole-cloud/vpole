using MongoDB.Driver;

namespace Pole.Features.DeploymentTargets.Contracts;

public sealed record NamedDomainReservationTargetRequest(
    string TenantId,
    string TechnicalName
);

public delegate Task<ResolveNamedDeploymentTargetOutcome> ResolveNamedDomainReservationTarget(
    NamedDomainReservationTargetRequest request,
    CancellationToken cancellationToken = default);

public sealed record DomainReservationTargetPlanningRequest(
    string TenantId,
    string DeploymentTargetId
);

public sealed record DomainReservationTargetPlanningSnapshot(
    DomainReservationTargetPlanningRequest Request,
    long TargetRevision,
    DeploymentTargetPublicIngressDto PublicIngress
);

public sealed record DomainReservationTargetPlanningUnavailable(
    DeploymentTargetDomainReservationAdmissionError Error
);

public union ResolveDomainReservationTargetPlanningSnapshotOutcome(
    DomainReservationTargetPlanningSnapshot,
    DomainReservationTargetPlanningUnavailable);

public delegate Task<ResolveDomainReservationTargetPlanningSnapshotOutcome>
    ResolveDomainReservationTargetPlanningSnapshot(
        DomainReservationTargetPlanningRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);

public sealed record DomainReservationTargetPlanningFenced;

public sealed record DomainReservationTargetPlanningChanged;

public union FenceDomainReservationTargetPlanningSnapshotOutcome(
    DomainReservationTargetPlanningFenced,
    DomainReservationTargetPlanningChanged);

public delegate Task<FenceDomainReservationTargetPlanningSnapshotOutcome> FenceDomainReservationTargetPlanningSnapshot(
    DomainReservationTargetPlanningSnapshot request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
