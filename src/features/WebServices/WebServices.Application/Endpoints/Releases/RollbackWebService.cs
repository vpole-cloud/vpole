using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints.Releases;

public static class RollbackWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapRollbackWebServiceEndpoint()
        {
            app.MapPost("/{Id}/rollback", async (
                    IMediator mediator,
                    IHttpProblemMapper httpProblems,
                    [AsParameters] RollbackWebServiceRequest request,
                    CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(
                    outcome => outcome switch
                    {
                        RollbackWebServiceResponse value =>
                            Results.Accepted(value: value),
                        RollbackWebServiceError error =>
                            httpProblems.Map(error).ToHttpResult()
                    }))
                .WithName("RollbackWebService")
                .Produces<RollbackWebServiceResponse>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record RollbackWebServiceRequest(
    [FromRoute] string Id,
    [FromBody] RollbackWebServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default)
    : IMediatorRequest<RollbackWebServiceOutcome>, ITenantWriteOperation;

public sealed record RollbackWebServiceRequestBody(long TargetRevision);

public sealed record RollbackWebServiceResponse(
    string Id,
    long ResourceRevision,
    string ReleaseId,
    IReadOnlyList<PlacementSummaryDto> Deployments);

public sealed class RollbackWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    LoadSucceededWebServiceRelease loadSucceededWebServiceRelease,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    RequestResourceDeploymentSetApply requestResourceDeploymentSetApply,
    PersistUpdatedWebServiceOutcome persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<RollbackWebServiceRequest, RollbackWebServiceOutcome>
{
    public async Task<RollbackWebServiceOutcome> HandleAsync(
        RollbackWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return Error(new RollbackWebServiceStateError(
                new WebServiceNotFound(request.Id)));
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Error(new RollbackWebServiceStateError(
                new WebServiceUpdateUnavailable(webService.Id)));

        var sourceRelease = await loadSucceededWebServiceRelease(
            request.TenantId,
            request.Id,
            request.Body.TargetRevision,
            cancellationToken);
        if (sourceRelease is null)
            return Error(new RollbackWebServiceStateError(
                new WebServiceReleaseNotFound(
                    webService.Id,
                    request.Body.TargetRevision)));

        var deployments = await loadResourceDeploymentSet(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (deployments.Count == 0)
            return Error(new RollbackWebServiceStateError(
                new WebServiceHasNoActiveDeployments(webService.Id)));

        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, webService.Spec);
        var targetQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, sourceRelease.Spec);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            WebServiceQuotaInput,
            RollbackWebServiceOutcome>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, targetQuota),
            quotaError => Error(quotaError),
            async (transaction, ct) =>
            {
                var updated = webService.UpdateDesiredState(sourceRelease.Spec, webService.Tags);
                var applied = await requestResourceDeploymentSetApply(
                    new ResourceDeploymentSetApplyRequest(
                        updated.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        updated.Id,
                        updated.ActiveRevision,
                        deployments.Select(deployment =>
                            WebServiceDeploymentWorkflow.ToSetApplyItem(deployment)).ToArray()),
                    transaction.Session,
                    ct);
                if (!applied.TryGetValue(out var updatedDeployments, out var applyFailure))
                    return transaction.Abort(Error(
                        new RollbackWebServiceError(applyFailure)));

                var persisted = await persistUpdatedWebService(
                    transaction.Session,
                    webService,
                    updated,
                    updated.ToUpdatedEvent(WebServiceDeploymentWorkflow.ToEventDeployments(updatedDeployments)),
                    ct);
                if (!persisted.TryGetValue(out _, out var persistenceError))
                    return transaction.Abort(Error(
                        new RollbackWebServiceError(persistenceError)));

                var release = WebServiceReleaseFactory.CreatePending(
                    updated.TenantId,
                    updated.Id,
                    updated.ActiveRevision,
                    WebServiceReleaseAction.Rollback,
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

                return transaction.Commit(new RollbackWebServiceOutcome(
                    new RollbackWebServiceResponse(
                        updated.Id,
                        updated.ActiveRevision,
                        release.Id,
                        updatedDeployments)));
            },
            cancellationToken);
    }

    private static RollbackWebServiceOutcome Error(
        RollbackWebServiceError error) =>
        new(error);
}

public sealed class RollbackWebServiceValidator : AbstractValidator<RollbackWebServiceRequest>
{
    public RollbackWebServiceValidator()
    {
        RuleFor(request => request.Id).NotEmpty();
        RuleFor(request => request.Body.TargetRevision).GreaterThan(0);
    }
}
