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
            app.MapPut("/{Id}", async (IMediator mediator, [AsParameters] UpdateWebServiceRequest request, CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(value => Results.Accepted(value: value));
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
    : IMediatorRequest<UpdateWebServiceResponse>, ITenantWriteOperation;

public sealed record UpdateWebServiceRequestBody(
    WebServiceSpecDto Spec,
    ImmutableHashSet<string> Tags);

public sealed record UpdateWebServiceResponse(
    string Id,
    long ResourceRevision,
    IReadOnlyList<PlacementSummaryDto> Deployments);

public sealed class UpdateWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    IResourceDeploymentSetQueryService deploymentQuery,
    IResourceDeploymentSetMutationWriter deploymentWriter,
    PersistUpdatedWebService persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider)
    : IMediatorHandler<UpdateWebServiceRequest, UpdateWebServiceResponse>
{
    public async Task<Result<UpdateWebServiceResponse>> HandleAsync(
        UpdateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be updated.");

        var deployments = await deploymentQuery.GetAllAsync(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (deployments.Count == 0)
            return Failures.Conflict("The web service has no active resource deployments.");

        var nextSpec = request.Body.Spec.ToDomain();
        var nextWebService = webService.UpdateDesiredState(nextSpec, request.Body.Tags);
        if (Equals(nextWebService, webService))
        {
            return new UpdateWebServiceResponse(
                webService.Id,
                webService.ActiveRevision,
                deployments);
        }

        var currentQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, webService.Spec);
        var nextQuota = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, nextSpec);
        return await quotaAdmissionExecutor.ExecuteAsync<
            WebServiceQuotaInput,
            UpdateWebServiceResponse>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(currentQuota, nextQuota),
            async (session, ct) =>
            {
                var physicalChanges = !Equals(webService.Spec, nextSpec);
                Result<IReadOnlyList<PlacementSummaryDto>> deploymentsResult = physicalChanges
                    ? await deploymentWriter.RequestSetApplyAsync(
                        new ResourceDeploymentSetApplyRequest(
                            webService.TenantId,
                            WebServiceResourceDeployment.ResourceType,
                            webService.Id,
                            nextWebService.ActiveRevision,
                            deployments.Select(deployment =>
                                WebServiceDeploymentWorkflow.ToSetApplyItem(deployment)).ToArray()),
                        session,
                        ct)
                    : await deploymentWriter.AdvanceResourceRevisionAsync(
                        new ResourceDeploymentResourceRevisionRequest(
                            webService.TenantId,
                            WebServiceResourceDeployment.ResourceType,
                            webService.Id,
                            nextWebService.ActiveRevision),
                        session,
                        ct);
                if (!deploymentsResult.TryGetValue(out var updatedDeployments, out var deploymentFailure))
                    return deploymentFailure;

                var persisted = await persistUpdatedWebService(
                    session,
                    webService,
                    nextWebService,
                    nextWebService.ToUpdatedEvent(
                        WebServiceDeploymentWorkflow.ToEventDeployments(updatedDeployments)),
                    ct);
                if (persisted is not ISuccess)
                    return (Failure)persisted;

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
                await persistCurrentWebServiceRelease(session, release, ct);

                return new UpdateWebServiceResponse(
                    nextWebService.Id,
                    nextWebService.ActiveRevision,
                    updatedDeployments);
            },
            cancellationToken);
    }
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
