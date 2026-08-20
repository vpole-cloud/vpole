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
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class RollbackWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapRollbackWebServiceEndpoint()
        {
            app.MapPost("/{Id}/rollback", async (IMediator mediator, [AsParameters] RollbackWebServiceRequest request, CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(value => Results.Accepted(value: value)))
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
    : IMediatorRequest<RollbackWebServiceResponse>, ITenantWriteOperation;

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
    RequestResourceDeploymentSetApplyLegacy requestResourceDeploymentSetApply,
    PersistUpdatedWebService persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider)
    : IMediatorHandler<RollbackWebServiceRequest, RollbackWebServiceResponse>
{
    public async Task<Result<RollbackWebServiceResponse>> HandleAsync(
        RollbackWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be rolled back.");

        var sourceRelease = await loadSucceededWebServiceRelease(
            request.TenantId,
            request.Id,
            request.Body.TargetRevision,
            cancellationToken);
        if (sourceRelease is null)
            return Failures.NotFound("Succeeded release revision was not found for web service.", request.Body.TargetRevision);

        var deployments = await loadResourceDeploymentSet(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (deployments.Count == 0)
            return Failures.Conflict("The web service has no active resource deployments.");

        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, webService.Spec);
        var targetQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, sourceRelease.Spec);
        return await quotaAdmissionExecutor.ExecuteAsync<
            WebServiceQuotaInput,
            RollbackWebServiceResponse>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, targetQuota),
            async (session, ct) =>
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
                    session,
                    ct);
                if (!applied.TryGetValue(out var updatedDeployments, out var applyFailure))
                    return applyFailure;

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
                await persistCurrentWebServiceRelease(session, release, ct);

                return new RollbackWebServiceResponse(
                    updated.Id,
                    updated.ActiveRevision,
                    release.Id,
                    updatedDeployments);
            },
            cancellationToken);
    }
}

public sealed class RollbackWebServiceValidator : AbstractValidator<RollbackWebServiceRequest>
{
    public RollbackWebServiceValidator()
    {
        RuleFor(request => request.Id).NotEmpty();
        RuleFor(request => request.Body.TargetRevision).GreaterThan(0);
    }
}
