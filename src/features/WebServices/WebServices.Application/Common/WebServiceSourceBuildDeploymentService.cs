using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Common;

public sealed class WebServiceSourceBuildDeploymentService(
    IWebServiceDbContext dbContext,
    IResourceDeploymentMutationWriter deploymentWriter)
    : IWebServiceSourceBuildDeploymentService
{
    public async Task<Result<SourceBuildDeploymentResult>> DeployAsync(
        SourceBuildDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<WebService>.Filter.And(
            Builders<WebService>.Filter.Eq(service => service.TenantId, request.TenantId),
            Builders<WebService>.Filter.Eq(service => service.Id, request.WebServiceId));
        var webService = await dbContext.WebServices
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.WebServiceId);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot accept a source build.");

        return await dbContext.RunInTransactionAsync<SourceBuildDeploymentResult>(async (session, ct) =>
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

            var update = await dbContext.WebServices.ReplaceOneAsync(
                session,
                Builders<WebService>.Filter.And(
                    filter,
                    Builders<WebService>.Filter.Eq(
                        item => item.Revision,
                        webService.Revision)),
                updatedWebService,
                cancellationToken: ct);
            if (update.ModifiedCount != 1)
                return Failures.Conflict("The web service changed while creating the source deployment.");

            var deploymentResult = await deploymentWriter.RequestApplyAsync(
                new ResourceDeploymentMutationCommand(
                    webService.TenantId,
                    ResourceDeploymentResourceTypes.WebService,
                    webService.Id),
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
                nextSpecSnapshot,
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
            await WebServiceReleasePersistence.InsertCurrentAsync(
                dbContext,
                session,
                release,
                ct);
            var changes = new WebServiceUpdatedEventChanges(
                UpdateLog<WebServiceSpecSnapshot>.Of(previousSpec, nextSpecSnapshot),
                UpdateLog<ImmutableHashSet<string>>.Of(webService.Tags, webService.Tags));
            await dbContext.StoreEventAsync(
                new WebServiceUpdatedEvent(
                    webService.Id,
                    webService.Name,
                    webService.ResourceGroupId,
                    webService.DnsRecord,
                    changes)
                {
                    EventCore = eventCore
                },
                session,
                ct);

            return new SourceBuildDeploymentResult(
                release.Id,
                updatedWebService.ActiveRevision);
        }, cancellationToken: cancellationToken);
    }
}
