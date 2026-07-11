using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Common;

public sealed class WebServiceSourceBuildDeploymentService(IWebServiceDbContext dbContext)
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

        return await dbContext.RunInTransactionAsync<SourceBuildDeploymentResult>(async (session, ct) =>
        {
            var nextRevision = Math.Max(webService.ActiveRevision, 1) + 1;
            var previousSpec = webService.Spec.ToSnapshot();
            var previousImageReference = webService.Spec.Image.FullImageName;
            var nextImage = new ImageInfo(
                request.Image.RegistryHost,
                request.Image.ImageName,
                request.Image.Tag,
                request.Image.PullSecretName);
            var nextSpec = webService.Spec with { Image = nextImage };
            var nextSpecSnapshot = nextSpec.ToSnapshot();
            var eventCore = EventCore.Create(request.TenantId);
            var release = WebServiceReleaseFactory.CreatePending(
                request.TenantId,
                request.WebServiceId,
                nextRevision,
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
                    nextImage.FullImageName));

            var update = await dbContext.WebServices.UpdateOneAsync(
                session,
                filter,
                Builders<WebService>.Update
                    .Set(service => service.ActiveRevision, nextRevision)
                    .Set(service => service.Spec, nextSpec),
                cancellationToken: ct);
            if (update.ModifiedCount != 1)
                return Failures.Conflict("The web service changed while creating the source deployment.");

            await dbContext.WebServiceReleases.InsertOneAsync(session, release, cancellationToken: ct);
            var changes = new WebServiceUpdatedEventChanges(
                UpdateLog<WebServiceSpecSnapshot>.Of(previousSpec, nextSpecSnapshot),
                UpdateLog<ImmutableHashSet<string>>.Of(webService.Tags, webService.Tags));
            await dbContext.StoreEventAsync(
                [
                    new WebServiceUpdatedEvent(
                        webService.Id,
                        webService.Name,
                        webService.ResourceGroupId,
                        webService.DnsRecord,
                        changes)
                    {
                        EventCore = eventCore
                    },
                    release.ToDeploymentRequestedEvent(webService, eventCore)
                ],
                session,
                ct);

            return new SourceBuildDeploymentResult(release.Id, nextRevision);
        }, cancellationToken: cancellationToken);
    }
}
