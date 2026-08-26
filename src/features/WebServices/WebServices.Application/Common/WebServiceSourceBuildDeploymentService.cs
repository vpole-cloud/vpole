using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Common;

public sealed class WebServiceSourceBuildDeploymentService(
    LoadWebServiceById loadWebServiceById,
    PersistUpdatedWebServiceOutcome persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    RunMongoOutcomeTransaction<
        Result<SourceBuildDeploymentResult, SourceBuildWebServiceDeploymentError>>
        runMongoTransaction,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    RequestResourceDeploymentSetApply requestResourceDeploymentSetApply,
    TimeProvider timeProvider)
{
    public async Task<Result<SourceBuildDeploymentResult, SourceBuildWebServiceDeploymentError>>
        DeployAsync(
        SourceBuildDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(
            request.TenantId,
            request.WebServiceId,
            cancellationToken);
        if (webService is null)
        {
            return new Err<SourceBuildWebServiceDeploymentError>(
                new SourceBuildWebServiceNotFound(request.WebServiceId));
        }
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
        {
            return new Err<SourceBuildWebServiceDeploymentError>(
                new SourceBuildWebServiceDeleting(request.WebServiceId));
        }

        var deployments = await loadResourceDeploymentSet(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (deployments.Count == 0)
        {
            return new Err<SourceBuildWebServiceDeploymentError>(
                new SourceBuildWebServiceHasNoActiveDeployments(request.WebServiceId));
        }

        return await runMongoTransaction(async (transaction, ct) =>
        {
            var session = transaction.Session;
            var previousImageReference = webService.Spec.Image.FullImageName;
            var nextImage = new ImageInfo(
                request.Image.RegistryHost,
                request.Image.ImageName,
                request.Image.Tag,
                request.Image.PullSecretName);
            var nextSpec = webService.Spec with { Image = nextImage };
            var updatedWebService = webService.UpdateDesiredState(
                nextSpec,
                webService.Tags);
            var deploymentResult = await requestResourceDeploymentSetApply(
                new ResourceDeploymentSetApplyRequest(
                    webService.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    webService.Id,
                    updatedWebService.ActiveRevision,
                    deployments.Select(deployment =>
                        WebServiceDeploymentWorkflow.ToSetApplyItem(deployment)).ToArray()),
                session,
                ct);
            if (!deploymentResult.TryGetValue(out var updatedDeployments, out var deploymentFailure))
            {
                return transaction.Abort(
                    new Err<SourceBuildWebServiceDeploymentError>(
                        ToDeploymentError(request.WebServiceId, deploymentFailure)));
            }

            var updatedEvent = updatedWebService.ToUpdatedEvent(
                WebServiceDeploymentWorkflow.ToEventDeployments(updatedDeployments));
            var update = await persistUpdatedWebService(
                session,
                webService,
                updatedWebService,
                updatedEvent,
                ct);
            if (update is Err<WebServiceUpdateConflict> updateConflict)
            {
                return transaction.Abort(
                    new Err<SourceBuildWebServiceDeploymentError>(
                        new SourceBuildWebServiceUpdateConflict(
                            updateConflict.Value.WebServiceId)));
            }

            var release = WebServiceReleaseFactory.CreatePending(
                request.TenantId,
                request.WebServiceId,
                updatedWebService.ActiveRevision,
                WebServiceReleaseAction.SourceBuild,
                request.ActorUserId,
                request.ActorEmail,
                nextSpec,
                WebServiceDeploymentWorkflow.ToReleaseDeployments(updatedDeployments),
                updatedEvent.EventCore.TraceId,
                updatedEvent.EventCore.CorrelationId,
                new WebServiceReleaseFactory.CreateOptions(
                    MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                    timeProvider.GetUtcNow().UtcDateTime),
                new SourceBuildReleaseProvenance(
                    request.BuildJobId,
                    request.SourceRepositoryId,
                    request.CommitSha,
                    request.Image.Digest,
                    previousImageReference,
                    nextImage.FullImageName));
            await persistCurrentWebServiceRelease(
                session,
                release,
                ct);
            return transaction.Commit(
                new Ok<SourceBuildDeploymentResult>(new SourceBuildDeploymentResult(
                    release.Id,
                    updatedWebService.ActiveRevision)));
        }, cancellationToken);
    }

    private static SourceBuildWebServiceDeploymentRejected ToDeploymentError(
        string webServiceId,
        ResourceDeploymentMutationError error) => error switch
        {
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
