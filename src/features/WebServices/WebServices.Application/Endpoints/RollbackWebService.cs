using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class RollbackWebService
{
    public static IEndpointRouteBuilder MapRollbackWebServiceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/rollback", async (IMediator mediator, [AsParameters] RollbackWebServiceRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(x => Results.Accepted(value: x));
            })
            .WithName("RollbackWebService")
            .Produces<RollbackWebServiceResponse>(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed record RollbackWebServiceRequest(
    [FromRoute] string Id,
    [FromBody] RollbackWebServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<RollbackWebServiceResponse>, ITenantWriteOperation;

public sealed record RollbackWebServiceRequestBody(int TargetRevision);

public sealed record RollbackWebServiceResponse(
    string Id,
    int Revision,
    string ReleaseId,
    PlacementSummaryDto Placement);

public sealed class RollbackWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    LoadSucceededWebServiceRelease loadSucceededWebServiceRelease,
    PersistUpdatedWebService persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    IResourceDeploymentMutationWriter deploymentWriter)
    : IMediatorHandler<RollbackWebServiceRequest, RollbackWebServiceResponse>
{
    public async Task<Result<RollbackWebServiceResponse>> HandleAsync(
        RollbackWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(
            request.TenantId,
            request.Id,
            cancellationToken);

        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);
        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be rolled back.");

        var targetRelease = await loadSucceededWebServiceRelease(
            request.TenantId,
            request.Id,
            request.Body.TargetRevision,
            cancellationToken);

        if (targetRelease is null)
            return Failures.NotFound("Succeeded release revision was not found for web service.", request.Body.TargetRevision);

        var targetSpec = targetRelease.Spec.ToDomain();
        return await quotaAdmissionExecutor.ExecuteAsync<WebServiceSpec, RollbackWebServiceResponse>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceSpec>(webService.Spec, targetSpec),
            async (session, transactionCancellationToken) =>
            {
                var updatedWebService = webService.UpdateDesiredState(
                    targetSpec,
                    webService.Tags);
                var deploymentEventCore = EventCore.Create(webService.TenantId);
                var changes = new WebServiceUpdatedEventChanges(
                    UpdateLog<WebServiceSpecSnapshot>.Of(webService.Spec.ToSnapshot(), targetRelease.Spec),
                    UpdateLog<ImmutableHashSet<string>>.Of(webService.Tags, webService.Tags));

                var updatedEvent = new WebServiceUpdatedEvent(
                    webService.Id,
                    webService.Name,
                    webService.ResourceGroupId,
                    webService.DnsRecord,
                    changes)
                {
                    EventCore = EventCore.Create(webService.TenantId)
                };
                var replace = await persistUpdatedWebService(
                    session,
                    webService,
                    updatedWebService,
                    updatedEvent,
                    transactionCancellationToken);
                if (replace is not ISuccess)
                    return (Failure)replace;

                var deploymentResult = await deploymentWriter.RequestApplyAsync(
                    new ResourceDeploymentMutationCommand(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id),
                    session,
                    transactionCancellationToken);
                if (!deploymentResult.TryGetValue(
                        out var deployment,
                        out var deploymentFailure))
                {
                    return deploymentFailure;
                }

                var release = WebServiceReleaseFactory.CreatePending(
                    webService.TenantId,
                    webService.Id,
                    updatedWebService.ActiveRevision,
                    WebServiceReleaseAction.Rollback,
                    request.ActorUserId.Value,
                    request.ActorEmail.Value,
                    targetRelease.Spec,
                    deploymentEventCore.TraceId,
                    deploymentEventCore.CorrelationId,
                    deploymentEventCore.OccurredOn,
                    deploymentGeneration: deployment.DesiredGeneration);
                await persistCurrentWebServiceRelease(
                    session,
                    release,
                    transactionCancellationToken);

                return new RollbackWebServiceResponse(
                    webService.Id,
                    updatedWebService.ActiveRevision,
                    release.Id,
                    deployment.ToPlacementSummaryDto());
            },
            cancellationToken);
    }
}

public sealed class RollbackWebServiceValidator : AbstractValidator<RollbackWebServiceRequest>
{
    public RollbackWebServiceValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.TargetRevision).GreaterThan(0);
    }
}
