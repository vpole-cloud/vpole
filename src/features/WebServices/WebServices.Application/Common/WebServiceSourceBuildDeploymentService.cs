using System.Collections.Immutable;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Common;

public sealed class WebServiceSourceBuildDeploymentService(
    LoadWebServiceById loadWebServiceById,
    PersistUpdatedWebService persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    RunMongoTransaction<SourceBuildDeploymentResult> runMongoTransaction,
    IResourceDeploymentComputeLookup deploymentComputeLookup,
    IResourceDeploymentMutationWriter deploymentWriter)
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

        var computeResult = await deploymentComputeLookup.GetCurrentAsync(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (!computeResult.TryGetValue(out var currentCompute, out var computeFailure))
            return computeFailure;

        return await runMongoTransaction(async (session, ct) =>
        {
            var previousSpec = webService.Spec.ToSnapshot();
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
            var nextSpecSnapshot = nextSpec.ToSnapshot();
            var eventCore = EventCore.Create(request.TenantId);

            var changes = new WebServiceUpdatedEventChanges(
                UpdateLog<WebServiceSpecSnapshot>.Of(previousSpec, nextSpecSnapshot),
                UpdateLog<ImmutableHashSet<string>>.Of(webService.Tags, webService.Tags));
            var updatedEvent = new WebServiceUpdatedEvent(
                webService.Id,
                webService.Name,
                webService.ResourceGroupId,
                webService.DnsRecord.ToSnapshot(),
                changes)
            {
                EventCore = eventCore
            };
            var update = await persistUpdatedWebService(
                session,
                webService,
                updatedWebService,
                updatedEvent,
                ct);
            if (update is not ISuccess)
                return (Failure)update;

            var deploymentResult = await deploymentWriter.RequestApplyAsync(
                new ResourceDeploymentMutationRequest(
                    webService.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    webService.Id,
                    currentCompute.Compute.Profile),
                session,
                ct);
            if (!deploymentResult.TryGetValue(
                    out var deployment,
                    out var deploymentFailure))
            {
                return deploymentFailure;
            }

            var release = WebServiceReleaseFactory.CreatePending(
                request.TenantId,
                request.WebServiceId,
                updatedWebService.ActiveRevision,
                WebServiceReleaseAction.SourceBuild,
                request.ActorUserId,
                request.ActorEmail,
                nextSpec,
                eventCore.TraceId,
                eventCore.CorrelationId,
                eventCore.OccurredOn,
                new SourceBuildReleaseProvenance(
                    request.BuildJobId,
                    request.SourceRepositoryId,
                    request.CommitSha,
                    request.Image.Digest,
                    previousImageReference,
                    nextImage.FullImageName),
                deployment.DesiredGeneration);
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
