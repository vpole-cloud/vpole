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
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints.Releases;

public static class RedeployWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapRedeployWebServiceEndpoint()
        {
            app.MapPost("/{Id}/redeploy", async (
                    IMediator mediator,
                    IHttpProblemMapper httpProblems,
                    [AsParameters] RedeployWebServiceRequest request,
                    CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(
                    outcome => outcome switch
                    {
                        RedeployWebServiceResponse value =>
                            Results.Accepted(value: value),
                        RedeployWebServiceError error =>
                            httpProblems.Map(error).ToHttpResult()
                    }))
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
    : IMediatorRequest<RedeployWebServiceOutcome>, ITenantWriteOperation;

public sealed record RedeployWebServiceResponse(
    string Id,
    long ResourceRevision,
    string ReleaseId,
    IReadOnlyList<PlacementSummaryDto> Deployments);

public union RedeployWebServiceOutcome(
    RedeployWebServiceResponse,
    RedeployWebServiceError);

public sealed class RedeployWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    RequestResourceDeploymentSetApply requestResourceDeploymentSetApply,
    PersistUpdatedWebServiceOutcome persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    RunMongoOutcomeTransaction<RedeployWebServiceOutcome> runMongoTransaction,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<RedeployWebServiceRequest, RedeployWebServiceOutcome>
{
    public async Task<RedeployWebServiceOutcome> HandleAsync(
        RedeployWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return Error(new WebServiceLifecycleMutationStateError(
                new WebServiceNotFound(request.Id)));
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Error(new WebServiceLifecycleMutationStateError(
                new WebServiceUpdateUnavailable(webService.Id)));

        var deploymentInventory = await loadResourceDeploymentSet(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        var deployments = deploymentInventory
            .Where(WebServiceDeploymentWorkflow.IsDesiredPlacement)
            .ToArray();
        if (deployments.Length == 0)
            return Error(new WebServiceLifecycleMutationStateError(
                new WebServiceHasNoActiveDeployments(webService.Id)));

        return await runMongoTransaction(async (transaction, ct) =>
        {
            var updated = webService.Redeploy();
            var deploymentsResult = await requestResourceDeploymentSetApply(
                new ResourceDeploymentSetApplyRequest(
                    webService.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    webService.Id,
                    updated.ActiveRevision,
                        deployments.Select(deployment =>
                        WebServiceDeploymentWorkflow.ToSetApplyItem(deployment)).ToArray()),
                transaction.Session,
                ct);
            if (!deploymentsResult.TryGetValue(out var updatedDeployments, out var deploymentsFailure))
                return transaction.Abort(Error(
                    new RedeployWebServiceError(deploymentsFailure)));

            var persisted = await persistUpdatedWebService(
                transaction.Session,
                webService,
                updated,
                updated.ToUpdatedEvent(WebServiceDeploymentWorkflow.ToEventDeployments(updatedDeployments)),
                ct);
            if (!persisted.TryGetValue(out _, out var persistenceError))
                return transaction.Abort(Error(
                    new RedeployWebServiceError(persistenceError)));

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
            await persistCurrentWebServiceRelease(transaction.Session, release, ct);

            return transaction.Commit(new RedeployWebServiceOutcome(
                new RedeployWebServiceResponse(
                    updated.Id,
                    updated.ActiveRevision,
                    release.Id,
                    updatedDeployments)));
        }, cancellationToken);
    }

    private static RedeployWebServiceOutcome Error(
        RedeployWebServiceError error) =>
        new(error);
}

public sealed class RedeployWebServiceValidator : AbstractValidator<RedeployWebServiceRequest>
{
    public RedeployWebServiceValidator() => RuleFor(request => request.Id).NotEmpty();
}
