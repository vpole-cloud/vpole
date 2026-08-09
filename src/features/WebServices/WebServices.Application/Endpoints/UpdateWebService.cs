using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

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
                    return result.ToHttpResult(x => Results.Accepted(value: x));
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
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<UpdateWebServiceResponse>, ITenantWriteOperation;

public sealed record UpdateWebServiceRequestBody(
    WebServiceSpecDto Spec,
    ImmutableHashSet<string> Tags,
    string? ComputeProfileId = null
);

public sealed record UpdateWebServiceResponse(
    string Id,
    PlacementSummaryDto Placement);

public sealed class UpdateWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    PersistUpdatedWebService persistUpdatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    ResolveComputeProfile resolveComputeProfile,
    IResourceDeploymentComputeLookup deploymentComputeLookup,
    IResourceDeploymentMutationWriter deploymentWriter)
    : IMediatorHandler<UpdateWebServiceRequest, UpdateWebServiceResponse>
{
    public async Task<Result<UpdateWebServiceResponse>> HandleAsync(
        UpdateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(
            request.TenantId,
            request.Id,
            cancellationToken);

        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);

        if (webService.Lifecycle is not ActiveWebServiceLifecycle)
            return Failures.Conflict("A deleting web service cannot be updated.");

        var nextSpec = request.Body.Spec.ToDomain();
        var computeLookupResult = await deploymentComputeLookup.GetCurrentAsync(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (!computeLookupResult.TryGetValue(
                out var currentCompute,
                out var computeLookupFailure))
            return computeLookupFailure;

        ResolvedComputeProfile? replacementCompute = null;
        if (!string.IsNullOrWhiteSpace(request.Body.ComputeProfileId))
        {
            var computeResult = await resolveComputeProfile(
                new ComputeProfileResolutionRequest(
                    request.Body.ComputeProfileId,
                    currentCompute.DeploymentTargetId,
                    webService.TenantId,
                    WebServiceResourceDeployment.ResourceType),
                cancellationToken);
            if (!computeResult.TryGetValue(
                    out replacementCompute,
                    out var computeFailure))
            {
                return computeFailure;
            }
        }

        var currentQuotaInput = WebServiceQuotaInput.Create(
            currentCompute.Compute.Requests.CpuMillis,
            currentCompute.Compute.Requests.MemoryMi,
            webService.Spec);
        var nextQuotaInput = WebServiceQuotaInput.Create(
            replacementCompute?.Requests.CpuMillis ??
            currentCompute.Compute.Requests.CpuMillis,
            replacementCompute?.Requests.MemoryMi ??
            currentCompute.Compute.Requests.MemoryMi,
            nextSpec);
        return await quotaAdmissionExecutor.ExecuteAsync<
            WebServiceQuotaInput,
            UpdateWebServiceResponse>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new UpdateQuotaMutation<WebServiceQuotaInput>(
                currentQuotaInput,
                nextQuotaInput),
            async (session, transactionCancellationToken) =>
            {
                var updatedWebService = webService.UpdateDesiredState(
                    nextSpec,
                    request.Body.Tags);
                var changes = GetChanges(webService, updatedWebService);
                var deploymentEventCore = EventCore.Create(webService.TenantId);

                var updatedEvent = new WebServiceUpdatedEvent(
                    request.Id,
                    webService.Name,
                    webService.ResourceGroupId,
                    webService.DnsRecord.ToSnapshot(),
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
                    new ResourceDeploymentMutationRequest(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        currentCompute.Compute.Profile,
                        replacementCompute?.ToResourceDeploymentConfiguration()),
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
                    WebServiceReleaseAction.Update,
                    request.ActorUserId.Value,
                    request.ActorEmail.Value,
                    updatedWebService.Spec,
                    deploymentEventCore.TraceId,
                    deploymentEventCore.CorrelationId,
                    deploymentEventCore.OccurredOn,
                    deploymentGeneration: deployment.DesiredGeneration);
                await persistCurrentWebServiceRelease(
                    session,
                    release,
                    transactionCancellationToken);

                return new UpdateWebServiceResponse(
                    webService.Id,
                    deployment);
            },
            cancellationToken);
    }

    private static WebServiceUpdatedEventChanges GetChanges(
        WebService webService,
        WebService updatedWebService) =>
        new(UpdateLog<WebServiceSpecSnapshot>.Of(
                webService.Spec.ToSnapshot(),
                updatedWebService.Spec.ToSnapshot()),
            UpdateLog<ImmutableHashSet<string>>.Of(webService.Tags,
                updatedWebService.Tags));
}

public sealed class UpdateWebServiceValidator : AbstractValidator<UpdateWebServiceRequest>
{
    public UpdateWebServiceValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty().WithMessage("Web service ID is required.");

        RuleFor(x => x.Body.Spec)
            .NotNull().WithMessage("Web service specification is required.")
            .SetValidator(new WebServiceSpecDtoValidator());
        RuleFor(x => x.Body.ComputeProfileId)
            .OptionalMongoObjectId("Compute profile ID");
    }
}
