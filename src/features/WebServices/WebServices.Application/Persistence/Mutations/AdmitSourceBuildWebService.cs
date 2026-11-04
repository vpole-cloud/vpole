using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence.Mutations;

public delegate Task<WebService?> LoadWebServiceForSourceBuild(
    SourceBuildTargetRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

internal sealed class MongoAdmitSourceBuildWebService(
    IMongoCollection<WebService> webServices,
    LoadResourceDeploymentPlanningSnapshot loadPlacements,
    RequestResourceDeploymentSetApply applyPlacements,
    PersistUpdatedWebServiceOutcome persist)
{
    public async Task<WebService?> LoadAsync(
        SourceBuildTargetRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        await webServices.Find(session, WebServiceFilters.ById(request.TenantId, request.WebServiceId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<LoadSourceBuildTargetOutcome> ReadAsync(
        SourceBuildTargetRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var service = await webServices.Find(session, WebServiceFilters.ById(request.TenantId, request.WebServiceId))
            .SingleOrDefaultAsync(cancellationToken);
        return service is null || service.Lifecycle is DeletingWebServiceLifecycle
            ? new SourceBuildWebServiceNotFound(request.WebServiceId)
            : new SourceBuildTargetSnapshot(service.TenantId, service.Id, service.Revision,
                service.Spec.Image.SourceBuildConfigurationId);
    }

    public async Task<AdmitSourceBuildWebServiceOutcome> InvokeAsync(
        AdmitSourceBuildWebServiceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Source build admission requires a transaction.");

        var service = await webServices.Find(session, WebServiceFilters.ById(request.TenantId, request.WebServiceId))
            .SingleOrDefaultAsync(cancellationToken);
        if (service is null || service.Lifecycle is DeletingWebServiceLifecycle ||
            request.Purpose == SourceBuildAdmissionPurpose.Build && service.Lifecycle is PreparedWebServiceLifecycle)
            return new SourceBuildWebServiceNotFound(request.WebServiceId);

        if (request.Purpose == SourceBuildAdmissionPurpose.Configure &&
            service.Spec.Image is UnboundSourceBuildImage && service.Lifecycle is PreparedWebServiceLifecycle)
        {
            // Manual creation explicitly selected build ownership; bind the server-allocated configuration ID.
            var next = service.AcceptConfigurationAwaitingArtifact(service.Spec with
            { Image = WebServiceImageFactory.Pending(request.ConfigurationId) }, service.Tags, service.Exposure);
            var read = await loadPlacements(new(request.TenantId, WebServiceResourceDeployment.ResourceType,
                request.WebServiceId), session, cancellationToken);
            if (read is not ResourceDeploymentPlanningSnapshot placements || placements.Items.Count == 0)
                return new SourceBuildWebServiceNotFound(request.WebServiceId);

            var applied = await applyPlacements(new(request.TenantId, WebServiceResourceDeployment.ResourceType,
                request.WebServiceId, next.ActiveRevision, placements.Items.Select(item =>
                    new ResourceDeploymentSetApplyItem(item.DeploymentId, item.Revision,
                        new(item.Compute.ComputeProfileId, item.Compute.ComputeProfileRevision), new PreserveIngress(),
                        FixedReplicas: item.FixedReplicas, PinnedCompute: true)).ToArray(),
                ResourceDeploymentActivation.KeepPrepared),
                session, cancellationToken);
            if (applied is not MutatedResourceDeployments changed)
                return new SourceBuildWebServiceNotFound(request.WebServiceId);

            var updatedEvent = next.ToUpdatedEvent(WebServiceDeploymentWorkflow.ToEventDeployments(changed.Placements));
            var updated = await persist(session, service, next, updatedEvent, cancellationToken);
            return updated is WebServiceUpdateAccepted
                ? new SourceBuildWebServiceAdmitted() : new SourceBuildWebServiceNotFound(request.WebServiceId);
        }

        if (service.Spec.Image.SourceBuildConfigurationId != request.ConfigurationId)
            return new SourceBuildWebServiceNotFound(request.WebServiceId);

        var result = await webServices.UpdateOneAsync(session, WebServiceFilters.AtPlanningVersion(service),
            Builders<WebService>.Update.Inc(item => item.PlanningFence, 1), cancellationToken: cancellationToken);
        return result.ModifiedCount == 1
            ? new SourceBuildWebServiceAdmitted() : new SourceBuildWebServiceNotFound(request.WebServiceId);
    }
}
