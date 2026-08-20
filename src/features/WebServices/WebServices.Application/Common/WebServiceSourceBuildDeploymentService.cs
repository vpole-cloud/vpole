using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Common;

public sealed class WebServiceSourceBuildDeploymentService(
    LoadWebServiceById loadWebServiceById,
    PersistUpdatedWebService persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    RunMongoTransaction<SourceBuildDeploymentResult> runMongoTransaction,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    RequestResourceDeploymentSetApplyLegacy requestResourceDeploymentSetApply,
    TimeProvider timeProvider)
    : IWebServiceSourceBuildDeploymentService
{
    public async Task<Result<SourceBuildDeploymentResult>> DeployAsync(
        SourceBuildDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(
            request.TenantId,
            request.WebServiceId,
            cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.WebServiceId);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot accept a source build.");

        var deployments = await loadResourceDeploymentSet(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (deployments.Count == 0)
            return Failures.Conflict("The web service has no active resource deployments.");

        return await runMongoTransaction(async (session, ct) =>
        {
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
                return deploymentFailure;

            var updatedEvent = updatedWebService.ToUpdatedEvent(
                WebServiceDeploymentWorkflow.ToEventDeployments(updatedDeployments));
            var update = await persistUpdatedWebService(
                session,
                webService,
                updatedWebService,
                updatedEvent,
                ct);
            if (update is not ISuccess)
                return (Failure)update;

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
            return new SourceBuildDeploymentResult(
                release.Id,
                updatedWebService.ActiveRevision);
        }, cancellationToken);
    }
}
