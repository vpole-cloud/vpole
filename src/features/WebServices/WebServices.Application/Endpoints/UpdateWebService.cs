using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
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

public static class UpdateWebService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapUpdateWebServiceEndpoint()
        {
            app.MapPut("/{Id}", async (
                    IMediator mediator,
                    IHttpProblemMapper httpProblems,
                    [AsParameters] UpdateWebServiceRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        UpdateWebServiceResponse value =>
                            Results.Accepted(value: value),
                        UpdateWebServiceError error =>
                            httpProblems.Map(error).ToHttpResult()
                    });
                })
                .WithName("UpdateWebService")
                .Produces<UpdateWebServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed record UpdateWebServiceRequest(
    [FromRoute] string Id,
    [FromBody] UpdateWebServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default)
    : IMediatorRequest<UpdateWebServiceOutcome>, ITenantWriteOperation;

public sealed record UpdateWebServiceRequestBody(
    WebServiceSpecDto Spec,
    ImmutableHashSet<string> Tags);

public sealed record UpdateWebServiceResponse(
    string Id,
    long ResourceRevision,
    IReadOnlyList<PlacementSummaryDto> Deployments);

public sealed class UpdateWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    RequestResourceDeploymentSetApply requestResourceDeploymentSetApply,
    AdvanceResourceDeploymentRevision advanceResourceDeploymentRevision,
    PersistUpdatedWebServiceOutcome persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<UpdateWebServiceRequest, UpdateWebServiceOutcome>
{
    public async Task<UpdateWebServiceOutcome> HandleAsync(
        UpdateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return Error(new WebServiceLifecycleMutationStateError(
                new WebServiceNotFound(request.Id)));
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Error(new WebServiceLifecycleMutationStateError(
                new WebServiceUpdateUnavailable(webService.Id)));

        var deployments = await loadResourceDeploymentSet(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (deployments.Count == 0)
            return Error(new WebServiceLifecycleMutationStateError(
                new WebServiceHasNoActiveDeployments(webService.Id)));

        var nextSpec = request.Body.Spec.ToDomain();
        var nextWebService = webService.UpdateDesiredState(nextSpec, request.Body.Tags);
        if (Equals(nextWebService, webService))
        {
            return new UpdateWebServiceOutcome(new UpdateWebServiceResponse(
                webService.Id,
                webService.ActiveRevision,
                deployments));
        }

        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, webService.Spec);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, nextSpec);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            WebServiceQuotaInput,
            UpdateWebServiceOutcome>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
            quotaError => Error(quotaError),
            async (transaction, ct) =>
            {
                var physicalChanges = !Equals(webService.Spec, nextSpec);
                IReadOnlyList<PlacementSummaryDto> updatedDeployments;
                if (physicalChanges)
                {
                    var deploymentsResult = await requestResourceDeploymentSetApply(
                        new ResourceDeploymentSetApplyRequest(
                            webService.TenantId,
                            WebServiceResourceDeployment.ResourceType,
                            webService.Id,
                            nextWebService.ActiveRevision,
                            deployments.Select(deployment =>
                                WebServiceDeploymentWorkflow.ToSetApplyItem(deployment)).ToArray()),
                        transaction.Session,
                        ct);
                    if (!deploymentsResult.TryGetValue(
                            out var physicalDeployments,
                            out var deploymentError))
                    {
                        return transaction.Abort(
                            Error(new UpdateWebServiceError(deploymentError)));
                    }
                    updatedDeployments = physicalDeployments;
                }
                else
                {
                    var deploymentsResult = await advanceResourceDeploymentRevision(
                        new ResourceDeploymentResourceRevisionRequest(
                            webService.TenantId,
                            WebServiceResourceDeployment.ResourceType,
                            webService.Id,
                            nextWebService.ActiveRevision),
                        transaction.Session,
                        ct);
                    if (!deploymentsResult.TryGetValue(
                            out var revisionDeployments,
                            out var revisionError))
                    {
                        return transaction.Abort(
                            Error(new UpdateWebServiceError(revisionError)));
                    }
                    updatedDeployments = revisionDeployments;
                }

                var persisted = await persistUpdatedWebService(
                    transaction.Session,
                    webService,
                    nextWebService,
                    nextWebService.ToUpdatedEvent(
                        WebServiceDeploymentWorkflow.ToEventDeployments(updatedDeployments)),
                    ct);
                if (!persisted.TryGetValue(out _, out var persistenceError))
                {
                    return transaction.Abort(
                        Error(new UpdateWebServiceError(persistenceError)));
                }

                var release = WebServiceReleaseFactory.CreatePending(
                    nextWebService.TenantId,
                    nextWebService.Id,
                    nextWebService.ActiveRevision,
                    WebServiceReleaseAction.Update,
                    request.ActorUserId.Value,
                    request.ActorEmail.Value,
                    nextWebService.Spec,
                    WebServiceDeploymentWorkflow.ToReleaseDeployments(updatedDeployments),
                    traceId: null,
                    correlationId: null,
                    new WebServiceReleaseFactory.CreateOptions(
                        MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                        timeProvider.GetUtcNow().UtcDateTime));
                await persistCurrentWebServiceRelease(transaction.Session, release, ct);

                return transaction.Commit(new UpdateWebServiceOutcome(
                    new UpdateWebServiceResponse(
                        nextWebService.Id,
                        nextWebService.ActiveRevision,
                        updatedDeployments)));
            },
            cancellationToken);
    }

    private static UpdateWebServiceOutcome Error(
        UpdateWebServiceError error) =>
        new(error);

}

public sealed class UpdateWebServiceValidator : AbstractValidator<UpdateWebServiceRequest>
{
    public UpdateWebServiceValidator()
    {
        RuleFor(request => request.Id).NotEmpty();
        RuleFor(request => request.Body.Spec)
            .NotNull().WithMessage("Web service specification is required.")
            .SetValidator(new WebServiceSpecDtoValidator());
    }
}
