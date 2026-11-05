using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Application;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Admission;

internal sealed class MongoDeploymentTargetPlanningSnapshots(
    IMongoCollection<DeploymentTarget> targets,
    FenceDeploymentTargetWorkloadConfigurations fence)
{
    public async Task<ResolveDeploymentTargetPlanningSnapshotOutcome> ReadAsync(
        DeploymentTargetAdmissionRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        RequireTransaction(session);
        var id = DeploymentTargetAdmissionSupport.NormalizeDeploymentTargetId(request.ExplicitDeploymentTargetId);
        var target = await targets.Find(session, DeploymentTargetFilters.ById(id)).FirstOrDefaultAsync(ct);
        if (target is null)
            return new DeploymentTargetPlanningUnavailable(new DeploymentTargetWorkloadAdmissionError(
                new DeploymentTargetNotFound(id)));

        var admission = DeploymentTargetAdmissionSupport.AdmitForWorkload(target);
        if (admission is DeploymentTargetWorkloadAdmissionError unavailable)
            return new DeploymentTargetPlanningUnavailable(unavailable);

        var lookup = (DeploymentTargetLookupDto)admission.Value;
        if (!DeploymentTargetAdmissionSupport.HasRequiredCapabilities(lookup, request.RequiredCapabilities,
                request.RequiredNetworkSource))
            return new DeploymentTargetPlanningUnavailable(new DeploymentTargetRequiredCapabilitiesUnavailable(
                lookup.Id, request.RequiredCapabilities));

        return new DeploymentTargetPlanningSnapshot(request with { ExplicitDeploymentTargetId = lookup.Id }, lookup);
    }

    public async Task<FenceDeploymentTargetPlanningSnapshotOutcome> FenceAsync(
        DeploymentTargetPlanningSnapshot request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var read = await ReadAsync(request.Request, session, ct);
        if (read is not DeploymentTargetPlanningSnapshot current || current.Target.Id != request.Target.Id ||
            current.Target.Revision != request.Target.Revision ||
            current.Target.WorkloadConfiguration.Revision != request.Target.WorkloadConfiguration.Revision)
            return new DeploymentTargetPlanningFenceRejected();

        var result = await fence([new(current.Target.Id, current.Target.WorkloadConfiguration.Revision)], session, ct);
        return result is DeploymentTargetWorkloadConfigurationsFenced
            ? new DeploymentTargetPlanningFenced()
            : new DeploymentTargetPlanningFenceRejected();
    }

    private static void RequireTransaction(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Planning snapshots require an active transaction.");
    }
}
