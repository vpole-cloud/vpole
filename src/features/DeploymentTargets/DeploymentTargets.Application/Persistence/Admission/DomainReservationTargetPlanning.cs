using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Admission;

internal sealed class MongoDomainReservationTargetPlanning(IMongoCollection<DeploymentTarget> targets)
{
    public async Task<ResolveNamedDeploymentTargetOutcome> ResolveNameAsync(
        NamedDomainReservationTargetRequest request,
        CancellationToken cancellationToken)
    {
        // Targets are platform-shared. Reservation admission has no workload type or isolation scope.
        // Tenant authorization remains with the request boundary and GitOps execution authority.
        var candidates = await targets.Find(DeploymentTargetFilters.ByTechnicalName(request.TechnicalName))
            .Limit(2).ToListAsync(cancellationToken);
        if (candidates.Count == 0)
            return new NamedDeploymentTargetUnavailable(request.TechnicalName);

        if (candidates.Count != 1)
            return new NamedDeploymentTargetAmbiguous(request.TechnicalName);

        var admission = DeploymentTargetAdmissionSupport.AdmitForWorkload(candidates[0]);
        return admission is DeploymentTargetLookupDto target &&
            DeploymentTargetAdmissionSupport.HasRequiredCapabilities(
                target, DeploymentTargetRequiredCapabilities.PublicIngress)
            ? target
            : new NamedDeploymentTargetUnavailable(request.TechnicalName);
    }

    public async Task<ResolveDomainReservationTargetPlanningSnapshotOutcome> ReadAsync(
        DomainReservationTargetPlanningRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        RequireTransaction(session);
        var target = await targets.Find(session, DeploymentTargetFilters.ById(request.DeploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);
        if (target is null)
            return Unavailable(new DeploymentTargetNotFound(request.DeploymentTargetId));

        var admission = DeploymentTargetAdmissionSupport.AdmitForWorkload(target);
        if (admission is DeploymentTargetWorkloadAdmissionError error)
            return error switch
            {
                DeploymentTargetNotFound missing => Unavailable(missing),
                DeploymentTargetDisabled disabled => Unavailable(disabled),
                DeploymentTargetDeletionRequested deleting => Unavailable(deleting),
                DeploymentTargetNewWorkloadsDisabled disabled => Unavailable(disabled)
            };

        var lookup = (DeploymentTargetLookupDto)admission.Value;
        return DeploymentTargetAdmissionSupport.HasRequiredCapabilities(
            lookup, DeploymentTargetRequiredCapabilities.PublicIngress)
            ? new DomainReservationTargetPlanningSnapshot(
                request, target.Revision, DeploymentTargetAdmissionSupport.ToPublicIngressDto(lookup))
            : Unavailable(new DeploymentTargetPublicIngressUnavailable(target.Id));
    }

    public async Task<FenceDomainReservationTargetPlanningSnapshotOutcome> FenceAsync(
        DomainReservationTargetPlanningSnapshot request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var read = await ReadAsync(request.Request, session, cancellationToken);
        if (read is not DomainReservationTargetPlanningSnapshot current ||
            current.TargetRevision != request.TargetRevision ||
            current.PublicIngress.WorkloadConfigurationRevision != request.PublicIngress.WorkloadConfigurationRevision)
            return new DomainReservationTargetPlanningChanged();

        var filter = DeploymentTargetFilters.AtDomainReservationPlanningVersion(request);
        var written = await targets.UpdateOneAsync(session, filter,
            Builders<DeploymentTarget>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        return written.ModifiedCount == 1
            ? new DomainReservationTargetPlanningFenced()
            : new DomainReservationTargetPlanningChanged();
    }

    private static DomainReservationTargetPlanningUnavailable Unavailable(
        DeploymentTargetDomainReservationAdmissionError error) => new(error);

    private static void RequireTransaction(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Domain reservation planning requires an active transaction.");
    }
}
