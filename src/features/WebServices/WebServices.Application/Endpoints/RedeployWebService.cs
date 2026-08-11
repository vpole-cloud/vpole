using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class RedeployWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapRedeployWebServiceEndpoint()
        {
            app.MapPost("/{Id}/redeploy", async (IMediator mediator, [AsParameters] RedeployWebServiceRequest request, CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(value => Results.Accepted(value: value)))
                .WithName("RedeployWebService")
                .Produces<RedeployWebServiceResponse>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record RedeployWebServiceRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default)
    : IMediatorRequest<RedeployWebServiceResponse>, ITenantWriteOperation;

public sealed record RedeployWebServiceResponse(
    string Id,
    long ResourceRevision,
    string ReleaseId,
    IReadOnlyList<PlacementSummaryDto> Deployments);

public sealed class RedeployWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    IResourceDeploymentSetQueryService deploymentQuery,
    IResourceDeploymentSetMutationWriter deploymentWriter,
    PersistUpdatedWebService persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    RunMongoTransaction<RedeployWebServiceResponse> runMongoTransaction,
    TimeProvider timeProvider)
    : IMediatorHandler<RedeployWebServiceRequest, RedeployWebServiceResponse>
{
    public async Task<Result<RedeployWebServiceResponse>> HandleAsync(
        RedeployWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be redeployed.");

        var deployments = await deploymentQuery.GetAllAsync(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (deployments.Count == 0)
            return Failures.Conflict("The web service has no active resource deployments.");

        return await runMongoTransaction(async (session, ct) =>
        {
            var updated = webService.Redeploy();
            var deploymentsResult = await deploymentWriter.RequestSetApplyAsync(
                new ResourceDeploymentSetApplyRequest(
                    webService.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    webService.Id,
                    updated.ActiveRevision,
                    deployments.Select(deployment =>
                        WebServiceDeploymentWorkflow.ToSetApplyItem(deployment)).ToArray()),
                session,
                ct);
            if (!deploymentsResult.TryGetValue(out var updatedDeployments, out var deploymentsFailure))
                return deploymentsFailure;

            var persisted = await persistUpdatedWebService(
                session,
                webService,
                updated,
                updated.ToUpdatedEvent(WebServiceDeploymentWorkflow.ToEventDeployments(updatedDeployments)),
                ct);
            if (persisted is not ISuccess)
                return (Failure)persisted;

            var release = WebServiceReleaseFactory.CreatePending(
                updated.TenantId,
                updated.Id,
                updated.ActiveRevision,
                WebServiceReleaseAction.Redeploy,
                request.ActorUserId.Value,
                request.ActorEmail.Value,
                updated.Spec,
                WebServiceDeploymentWorkflow.ToReleaseDeployments(updatedDeployments),
                traceId: null,
                correlationId: null,
                new WebServiceReleaseFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(),
                    timeProvider.GetUtcNow().UtcDateTime));
            await persistCurrentWebServiceRelease(session, release, ct);

            return new RedeployWebServiceResponse(
                updated.Id,
                updated.ActiveRevision,
                release.Id,
                updatedDeployments);
        }, cancellationToken);
    }
}

public sealed class RedeployWebServiceValidator : AbstractValidator<RedeployWebServiceRequest>
{
    public RedeployWebServiceValidator() => RuleFor(request => request.Id).NotEmpty();
}
