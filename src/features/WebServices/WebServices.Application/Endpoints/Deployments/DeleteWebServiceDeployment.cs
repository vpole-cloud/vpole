using FluentValidation;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using MongoDB.Bson;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class DeleteWebServiceDeployment
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapDeleteWebServiceDeploymentEndpoint()
        {
            app.MapDelete(
                    "/{Id}/deployments/{DeploymentId}",
                    async (
                        IMediator mediator,
                        [AsParameters] DeleteWebServiceDeploymentRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(value => Results.Accepted(value: value)))
                .WithName("DeleteWebServiceDeployment")
                .Produces<PlacementSummaryDto>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record DeleteWebServiceDeploymentRequest(
    [FromRoute] string Id,
    [FromRoute] string DeploymentId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<PlacementSummaryDto>, ITenantWriteOperation;

public sealed class DeleteWebServiceDeploymentHandler(
    LoadWebServiceById loadWebService,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    RequestExactResourceDeploymentDeletionLegacy requestExactResourceDeploymentDeletion,
    AdvanceResourceDeploymentRevisionLegacy advanceResourceDeploymentRevision,
    PersistUpdatedWebService persistWebService,
    PersistCurrentWebServiceRelease persistRelease,
    IQuotaAdmissionExecutor quotaAdmission,
    TimeProvider timeProvider)
    : IMediatorHandler<DeleteWebServiceDeploymentRequest, PlacementSummaryDto>
{
    public async Task<Result<PlacementSummaryDto>> HandleAsync(
        DeleteWebServiceDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebService(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);

        var deployments = await loadResourceDeploymentSet(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            cancellationToken);
        var current = deployments.SingleOrDefault(deployment =>
            deployment.DeploymentId == request.DeploymentId);
        if (current is null)
        {
            return Failures.NotFound(
                "Resource deployment not found.",
                request.DeploymentId);
        }
        if (string.Equals(
                current.DesiredOperation,
                "Delete",
                StringComparison.Ordinal))
        {
            return Failures.Conflict(
                "The web-service deployment is already being removed.");
        }

        var desiredDeployments = deployments.Where(deployment =>
            !string.Equals(
                deployment.DesiredOperation,
                "Delete",
                StringComparison.Ordinal)).ToArray();
        if (desiredDeployments.Length <= 1)
        {
            return Failures.Conflict(
                "The last web-service deployment cannot be removed.");
        }

        var remaining = desiredDeployments.Where(deployment =>
            deployment.DeploymentId != request.DeploymentId).ToArray();
        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(
            desiredDeployments,
            webService.Spec);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInput(
            remaining,
            webService.Spec);
        return await quotaAdmission.ExecuteAsync<WebServiceQuotaInput, PlacementSummaryDto>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
            async (session, token) =>
            {
                var updated = webService.AdvancePlacementDesiredState();
                var deleted = await requestExactResourceDeploymentDeletion(
                    new ResourceDeploymentExactMutationRequest(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        current.DeploymentId,
                        current.DeploymentRevision,
                        current.Compute.Profile,
                        ResourceRevision: updated.ActiveRevision),
                    session,
                    token);
                if (!deleted.TryGetValue(out var result, out var failure))
                    return failure;

                var stamped = await advanceResourceDeploymentRevision(
                    new ResourceDeploymentResourceRevisionRequest(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        updated.ActiveRevision),
                    session,
                    token);
                if (!stamped.TryGetValue(out var snapshots, out failure))
                    return failure;

                var retainedSnapshots = snapshots.Where(item =>
                    item.DeploymentId != current.DeploymentId).ToArray();
                var eventDeployments = WebServiceDeploymentWorkflow
                    .ToEventDeployments(retainedSnapshots)
                    .AddRange(WebServiceDeploymentWorkflow.ToEventDeployments(
                        [result],
                        WebServiceReleaseDeploymentOperation.Delete))
                    .OrderBy(item => item.ResourceDeploymentId, StringComparer.Ordinal)
                    .ToImmutableList();
                var saved = await persistWebService(
                    session,
                    webService,
                    updated,
                    updated.ToUpdatedEvent(eventDeployments),
                    token);
                if (saved is not ISuccess)
                    return (Failure)saved;

                var releaseDeployments = WebServiceDeploymentWorkflow
                    .ToReleaseDeployments(retainedSnapshots)
                    .AddRange(WebServiceDeploymentWorkflow.ToReleaseDeployments(
                        [result],
                        WebServiceReleaseDeploymentOperation.Delete));
                var release = WebServiceReleaseFactory.CreatePending(
                    updated.TenantId,
                    updated.Id,
                    updated.ActiveRevision,
                    WebServiceReleaseAction.RemoveDeployment,
                    null,
                    null,
                    updated.Spec,
                    releaseDeployments,
                    null,
                    null,
                    new WebServiceReleaseFactory.CreateOptions(
                        ObjectId.GenerateNewId().ToString(),
                        timeProvider.GetUtcNow().UtcDateTime));
                await persistRelease(session, release, token);
                return result;
            },
            cancellationToken);
    }
}

public sealed class DeleteWebServiceDeploymentValidator : AbstractValidator<DeleteWebServiceDeploymentRequest>
{
    public DeleteWebServiceDeploymentValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("Web service ID");
        RuleFor(x => x.DeploymentId).MongoObjectId("Resource deployment ID");
    }
}
