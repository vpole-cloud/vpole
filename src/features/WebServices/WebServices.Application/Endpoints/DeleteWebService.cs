using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class DeleteWebService
{
    public const string Route = "/{Id}";
    public static IEndpointRouteBuilder MapDeleteWebServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete(Route, async (
                IMediator mediator,
                [AsParameters] DeleteWebServiceRequest request,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(request, ct);
                return result.ToHttpResult(outcome => outcome switch
                {
                    DeleteWebServiceResponse value => Results.Accepted(value: value),
                    DeleteWebServiceError error => MapError(error)
                });
            })
            .WithName("DeleteWebService")
            .Produces(StatusCodes.Status202Accepted);

        return app;
    }
}

public union DeleteWebServiceOutcome(
    DeleteWebServiceResponse,
    DeleteWebServiceError);

public sealed class DeleteWebServiceHandler(
    LoadWebServiceById loadWebServiceById,
    ApplyWebServiceDeletion deletion,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    LoadResourceDeploymentSet loadResourceDeploymentSet)
    : IMediatorOutcomeHandler<DeleteWebServiceRequest, DeleteWebServiceOutcome>
{
    public async Task<DeleteWebServiceOutcome> HandleAsync(DeleteWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
            return Error(new DeleteWebServiceStateError(
                new WebServiceNotFound(request.Id)));
        if (webService.Lifecycle is DeletingWebServiceLifecycle)
        {
            var deletingPlacements = await loadResourceDeploymentSet(
                new LoadResourceDeploymentSetRequest(
                    TenantId: request.TenantId,
                    ResourceType: WebServiceResourceDeployment.ResourceType,
                    ResourceId: request.Id),
                cancellationToken: cancellationToken);
            return deletingPlacements.Count == 0
                ? Error(new DeleteWebServiceStateError(
                    new WebServiceHasNoActiveDeployments(request.Id)))
                : new DeleteWebServiceOutcome(
                    new DeleteWebServiceResponse(request.Id, deletingPlacements));
        }

        var deployments = await loadResourceDeploymentSet(
            new LoadResourceDeploymentSetRequest(
                TenantId: webService.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: webService.Id),
            cancellationToken: cancellationToken);
        if (deployments.Count == 0)
            return Error(new DeleteWebServiceStateError(
                new WebServiceHasNoActiveDeployments(webService.Id)));

        var quotaInput = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, webService.Spec);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            WebServiceQuotaInput,
            DeleteWebServiceOutcome>(
            new ExecuteQuotaAdmissionRequest<global::Pole.Features.WebServices.Application.Common.WebServiceQuotaInput,
                global::Pole.Features.WebServices.Application.Endpoints.DeleteWebServiceOutcome>(
                TenantId: webService.TenantId,
                ResourceKey: WebServiceQuotaResourceKey.Create(webService.Id),
                Mutation: new DeleteQuotaMutation<WebServiceQuotaInput>(quotaInput),
                QuotaError: quotaError => Error(new DeleteWebServiceError(quotaError)),
                MutationCallback: async (transaction, transactionCancellationToken) =>
                {
                    var result = await deletion(new(webService,
                        new(webService.TenantId, WebServiceResourceDeployment.ResourceType, webService.Id,
                            webService.ActiveRevision, deployments.Select(deployment =>
                                new ResourceDeploymentSetDeletionItem(deployment.DeploymentId,
                                    deployment.DeploymentRevision, deployment.Compute.Profile)).ToArray())),
                        transaction.Session, transactionCancellationToken);
                    return result switch
                    {
                        WebServiceDeletionApplied applied => transaction.Commit(new DeleteWebServiceOutcome(
                            new DeleteWebServiceResponse(request.Id, applied.Deployments))),
                        WebServiceUpdateConflict conflict =>
                            transaction.Abort(Error(new DeleteWebServiceError(conflict))),
                        WebServiceHostnameAttachmentRemovalRequired attachment =>
                            transaction.Abort(Error(new DeleteWebServiceError(attachment))),
                        SourceBuildWebServiceHasActiveJobs builds =>
                            transaction.Abort(Error(new DeleteWebServiceError(builds))),
                        ResourceDeploymentMutationError deployment =>
                            transaction.Abort(Error(new DeleteWebServiceError(deployment)))
                    };
                }),
            cancellationToken: cancellationToken);
    }

    private static DeleteWebServiceOutcome Error(DeleteWebServiceError error) =>
        new(error);
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
    [FromTenantHeader] string TenantId
) : IMediatorRequest<DeleteWebServiceOutcome>, ITenantWriteOperation;

public sealed record DeleteWebServiceResponse(
    string Id,
    IReadOnlyList<PlacementSummaryDto> Deployments
);
