using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Common;

public sealed class WebServiceSourceBuildDeploymentService(
    LoadWebServiceForSourceBuild loadService,
    RunMongoOutcomeTransaction<DeploySourceBuildWebServiceOutcome> runTransaction,
    LoadResourceDeploymentPlanningSnapshot loadPlacements,
    RequestResourceDeploymentSetApply applyPlacements,
    PersistWebServiceArtifactPromotion persistPromotion,
    BeginSourceBuildPromotion beginPromotion,
    CompleteSourceBuildPromotion completePromotion,
    PersistCurrentWebServiceRelease persistRelease,
    TimeProvider clock)
{
    public Task<DeploySourceBuildWebServiceOutcome> DeployAsync(
        SourceBuildDeploymentRequest request,
        CancellationToken cancellationToken = default) => runTransaction(async (transaction, ct) =>
    {
        var session = transaction.Session;
        var promotion = new SourceBuildPromotionRequest(request.TenantId, request.BuildJobId, request.WebServiceId,
            request.Authority, new(request.SourceRepositoryId, request.CommitSha,
                new($"{request.Image.RegistryHost}/{request.Image.ImageName}:{request.Image.Tag}",
                    request.Image.Digest)));
        var admission = await beginPromotion(promotion, session, ct);
        if (admission is SourceBuildAlreadyPromoted replay)
            return transaction.Commit(
                new SourceBuildDeploymentResult(replay.ReleaseId, replay.Revision));
        if (admission is not SourceBuildPromotionAuthorized)
            return transaction.Abort(Superseded(request.WebServiceId));

        var service = await loadService(new(request.TenantId, request.WebServiceId), session, ct);
        if (service is null)
            return transaction.Abort(new SourceBuildWebServiceDeploymentError(
                new SourceBuildWebServiceNotFound(request.WebServiceId)));
        if (service.Lifecycle is DeletingWebServiceLifecycle)
            return transaction.Abort(new SourceBuildWebServiceDeploymentError(
                new SourceBuildWebServiceDeleting(request.WebServiceId)));
        if (service.Lifecycle is not (ActiveWebServiceLifecycle or AwaitingFirstArtifactWebServiceLifecycle) ||
            service.Spec.Image.SourceBuildConfigurationId != request.Authority.ConfigurationId ||
            service.Spec.Image is DeliveredSourceBuildImage delivered &&
            delivered.Generation >= request.Authority.Generation)
            return transaction.Abort(Superseded(request.WebServiceId));

        var read = await loadPlacements(new(request.TenantId, WebServiceResourceDeployment.ResourceType,
            request.WebServiceId), session, ct);
        if (read is not ResourceDeploymentPlanningSnapshot placements || placements.Items.Count == 0)
            return transaction.Abort(new SourceBuildWebServiceDeploymentError(
                new SourceBuildWebServiceHasNoActiveDeployments(request.WebServiceId)));

        var activePlacements = placements.Items.Where(item => item.State != ResourceDeploymentPlanningState.Deleting)
            .ToArray();
        if (activePlacements.Length == 0)
            return transaction.Abort(new DeploySourceBuildWebServiceOutcome(new SourceBuildWebServiceDeploymentError(
                new SourceBuildWebServiceHasNoActiveDeployments(request.WebServiceId))));

        var artifact = DeliveredSourceBuildImage.Create(request.Authority.ConfigurationId,
            new ImageInfo(request.Image.RegistryHost, request.Image.ImageName, request.Image.Tag,
                request.Image.PullSecretName), request.Image.Digest, request.BuildJobId, request.Authority.Generation);
        var next = service.PromoteArtifact(artifact);
        var applied = await applyPlacements(new(request.TenantId, WebServiceResourceDeployment.ResourceType,
            request.WebServiceId, next.ActiveRevision, activePlacements.Select(item =>
                new ResourceDeploymentSetApplyItem(item.DeploymentId, item.Revision,
                    new(item.Compute.ComputeProfileId, item.Compute.ComputeProfileRevision), new PreserveIngress(),
                    FixedReplicas: item.FixedReplicas, PinnedCompute: true)).ToArray()), session, ct);
        if (applied is ResourceDeploymentMutationError error)
            return transaction.Abort(new SourceBuildWebServiceDeploymentError(
                ToDeploymentError(request.WebServiceId, error)));

        var changed = ((MutatedResourceDeployments)applied.Value).Placements;
        var updatedEvent = next.ToUpdatedEvent(WebServiceDeploymentWorkflow.ToEventDeployments(changed));
        var write = await persistPromotion(new(service, next, updatedEvent, promotion), session, ct);
        if (write is not WebServiceUpdateAccepted)
            return transaction.Abort(new SourceBuildWebServiceDeploymentError(
                new SourceBuildWebServiceUpdateConflict(service.Id,
                    SourceBuildWebServiceUpdateConflictReason.ExpectedRevisionConflict)));

        var release = WebServiceReleaseFactory.CreatePending(request.TenantId, request.WebServiceId,
            next.ActiveRevision, WebServiceReleaseAction.SourceBuild, request.ActorUserId, request.ActorEmail,
            next.Spec, WebServiceDeploymentWorkflow.ToReleaseDeployments(changed), updatedEvent.EventCore.TraceId,
            updatedEvent.EventCore.CorrelationId, new(MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                clock.GetUtcNow().UtcDateTime), next.Exposure, new SourceBuildReleaseProvenance(request.BuildJobId,
                request.SourceRepositoryId, request.CommitSha, request.Image.Digest,
                service.Spec.Image.ResolvedImageReference, artifact.ResolvedImageReference!));
        await persistRelease(session, release, ct);
        await completePromotion(new(promotion, release.Id, next.ActiveRevision), session, ct);
        return transaction.Commit(
            new SourceBuildDeploymentResult(release.Id, next.ActiveRevision));
    }, cancellationToken);

    private static DeploySourceBuildWebServiceOutcome Superseded(string serviceId) =>
        new SourceBuildWebServiceDeploymentError(new SourceBuildWebServiceUpdateConflict(serviceId,
            SourceBuildWebServiceUpdateConflictReason.SupersededBuild));

    private static SourceBuildWebServiceDeploymentRejected ToDeploymentError(
        string webServiceId,
        ResourceDeploymentMutationError error) => error switch
        {
            ResourceDeploymentConnectionConflict => new(
                webServiceId,
                new SourceBuildResourceDeploymentRejection(
                    new SourceBuildResourceDeploymentPlacementUnavailable(null))),
            ResourceDeploymentMutationNotFound notFound => new(
                webServiceId,
                new SourceBuildResourceDeploymentRejection(
                    new SourceBuildResourceDeploymentNotFound(
                        notFound.ResourceDeploymentId))),
            ResourceDeploymentChanged changed => new(
                webServiceId,
                new SourceBuildResourceDeploymentRejection(
                    new SourceBuildResourceDeploymentChanged(
                        changed.ResourceDeploymentId))),
            ResourceDeploymentCardinalityConflict => new(
                webServiceId,
                new SourceBuildResourceDeploymentRejection(
                    new SourceBuildResourceDeploymentCardinalityConflict())),
            ResourceDeploymentPendingDeletion pending => new(
                webServiceId,
                new SourceBuildResourceDeploymentRejection(
                    new SourceBuildResourceDeploymentPendingDeletion(
                        pending.ResourceDeploymentId))),
            ResourceDeploymentPlacementUnavailable unavailable => new(
                webServiceId,
                new SourceBuildResourceDeploymentRejection(
                    new SourceBuildResourceDeploymentPlacementUnavailable(
                        unavailable.ResourceDeploymentId))),
            ResourceDeploymentReplacementTargetMismatch mismatch => new(
                webServiceId,
                new SourceBuildResourceDeploymentRejection(
                    new SourceBuildResourceDeploymentReplacementTargetMismatch(
                        mismatch.ResourceDeploymentId))),
            ResourceDeploymentIngressConfigurationChanged ingressChanged => new(
                webServiceId,
                new SourceBuildResourceDeploymentRejection(
                    new SourceBuildResourceDeploymentIngressConfigurationChanged(
                        ingressChanged.ResourceDeploymentId))),
            ResourceDeploymentRetryUnavailable retryUnavailable => new(
                webServiceId,
                new SourceBuildResourceDeploymentRejection(
                    new SourceBuildResourceDeploymentRetryUnavailable(
                        retryUnavailable.ResourceDeploymentId))),
            ResourceDeploymentComputeConfigurationChanged => new(
                webServiceId,
                new SourceBuildResourceDeploymentRejection(
                    new SourceBuildResourceDeploymentComputeConfigurationChanged()))
        };
}
