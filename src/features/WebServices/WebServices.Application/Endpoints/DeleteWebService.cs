using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class DeleteWebService
{
    public const string Route = "/{Id}";
    public static IEndpointRouteBuilder MapDeleteWebServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete(Route, async (IMediator mediator, [AsParameters] DeleteWebServiceRequest request, CancellationToken ct) =>
            {
                var result = await mediator.Send(request, ct);
                return result.ToHttpResult(x => Results.Accepted(value: x));
            })
            .WithName("DeleteWebService")
            .Produces(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed class DeleteWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    BeginWebServiceDeletion beginWebServiceDeletion,
    ISourceBuildReferenceLookup sourceBuildReferences,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    IResourceDeploymentComputeLookup deploymentComputeLookup,
    IResourceDeploymentMutationWriter deploymentWriter,
    IResourceDeploymentQueryService deploymentQuery,
    TimeProvider timeProvider
    ) : IMediatorHandler<DeleteWebServiceRequest, DeleteWebServiceResponse>
{
    public async Task<Result<DeleteWebServiceResponse>> HandleAsync(DeleteWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var configurationGuard = await sourceBuildReferences.EnsureWebServiceCanBeDeletedAsync(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (configurationGuard is Failure failure)
            return failure;

        var webService = await loadWebServiceById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);
        if (webService.Lifecycle is DeletingWebServiceLifecycle)
        {
            var deletingPlacement = await deploymentQuery.GetAsync(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                cancellationToken);
            return deletingPlacement is null
                ? Failures.Conflict(
                    "The deleting web service has no resource deployment.")
                : new DeleteWebServiceResponse(request.Id, deletingPlacement);
        }

        var computeLookupResult = await deploymentComputeLookup.GetCurrentAsync(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (!computeLookupResult.TryGetValue(
                out var currentCompute,
                out var computeLookupFailure))
            return computeLookupFailure;

        var quotaInput = WebServiceQuotaInput.Create(
            currentCompute.Compute.Requests.CpuMillis,
            currentCompute.Compute.Requests.MemoryMi,
            webService.Spec);
        return await quotaAdmissionExecutor.ExecuteAsync<
            WebServiceQuotaInput,
            DeleteWebServiceResponse>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new DeleteQuotaMutation<WebServiceQuotaInput>(quotaInput),
            async (session, transactionCancellationToken) =>
            {
                var deleting = webService.BeginDeletion(
                    timeProvider.GetUtcNow().UtcDateTime);
                var replace = await beginWebServiceDeletion(
                    session,
                    webService,
                    deleting,
                    transactionCancellationToken);
                if (replace is not ISuccess)
                    return (Failure)replace;

                var deploymentResult = await deploymentWriter.RequestDeletionAsync(
                    new ResourceDeploymentMutationCommand(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        currentCompute.Compute.Profile),
                    session,
                    transactionCancellationToken);
                if (!deploymentResult.TryGetValue(
                        out var deployment,
                        out var deploymentFailure))
                {
                    return deploymentFailure;
                }

                return new DeleteWebServiceResponse(
                    request.Id,
                    deployment.ToPlacementSummaryDto());
            },
            cancellationToken);
    }
}

public sealed class FinalizeWebServiceDeletionConsumer(
    FinalizeWebServiceDeletion finalizeWebServiceDeletion)
    : IConsumer<ResourceDeploymentDeletedEvent>
{
    public Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context)
    {
        var message = context.Message;
        if (!string.Equals(
                message.ResourceType,
                WebServiceResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return finalizeWebServiceDeletion(
            message.EventCore.TenantId,
            message.ResourceId,
            context.CancellationToken);
    }
}

public sealed record DeleteWebServiceRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<DeleteWebServiceResponse>, ITenantWriteOperation;

public sealed record DeleteWebServiceResponse(
    string Id,
    PlacementSummaryDto Placement
);
