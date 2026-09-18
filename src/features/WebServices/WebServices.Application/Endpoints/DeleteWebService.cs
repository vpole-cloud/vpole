using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class DeleteWebService
{
    public const string Route = "/{Id}";
    public static IEndpointRouteBuilder MapDeleteWebServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete(Route, async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] DeleteWebServiceRequest request,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(request, ct);
                return result.ToHttpResult(outcome => outcome switch
                {
                    DeleteWebServiceResponse value => Results.Accepted(value: value),
                    DeleteWebServiceError error => httpProblems.Map(error).ToHttpResult()
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
    BeginWebServiceDeletionOutcome beginWebServiceDeletion,
    EnsureNoWebServiceActiveSourceBuilds ensureNoActiveSourceBuilds,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    RequestResourceDeploymentSetDeletion requestResourceDeploymentSetDeletion,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    TimeProvider timeProvider
    ) : IMediatorOutcomeHandler<DeleteWebServiceRequest, DeleteWebServiceOutcome>
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
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                cancellationToken);
            return deletingPlacements.Count == 0
                ? Error(new DeleteWebServiceStateError(
                    new WebServiceHasNoActiveDeployments(request.Id)))
                : new DeleteWebServiceOutcome(
                    new DeleteWebServiceResponse(request.Id, deletingPlacements));
        }

        var deployments = await loadResourceDeploymentSet(
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        if (deployments.Count == 0)
            return Error(new DeleteWebServiceStateError(
                new WebServiceHasNoActiveDeployments(webService.Id)));

        var quotaInput = WebServiceDeploymentWorkflow.ToQuotaInput(deployments, webService.Spec);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            WebServiceQuotaInput,
            DeleteWebServiceOutcome>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new DeleteQuotaMutation<WebServiceQuotaInput>(quotaInput),
            quotaError => Error(new DeleteWebServiceError(quotaError)),
            async (transaction, transactionCancellationToken) =>
            {
                var deleting = webService.BeginDeletion(
                    timeProvider.GetUtcNow().UtcDateTime);
                var replace = await beginWebServiceDeletion(
                    transaction.Session,
                    webService,
                    deleting,
                    transactionCancellationToken);
                if (!replace.TryGetValue(out _, out var replaceError))
                    return transaction.Abort(
                        Error(new DeleteWebServiceError(replaceError)));

                var buildGuard = await ensureNoActiveSourceBuilds(
                    request.TenantId, request.Id, transaction.Session, transactionCancellationToken);
                if (!buildGuard.TryGetValue(out _, out var buildError))
                    return transaction.Abort(Error(new DeleteWebServiceError(buildError)));

                var deploymentResult = await requestResourceDeploymentSetDeletion(
                    new ResourceDeploymentSetDeletionRequest(
                        webService.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        webService.Id,
                        webService.ActiveRevision,
                        deployments.Select(deployment => new ResourceDeploymentSetDeletionItem(
                            deployment.DeploymentId,
                            deployment.DeploymentRevision,
                            deployment.Compute.Profile)).ToArray()),
                    transaction.Session,
                    transactionCancellationToken);
                if (!deploymentResult.TryGetValue(
                        out var updatedDeployments,
                        out var deploymentError))
                {
                    return transaction.Abort(
                        Error(new DeleteWebServiceError(deploymentError)));
                }

                return transaction.Commit(new DeleteWebServiceOutcome(
                    new DeleteWebServiceResponse(
                        request.Id,
                        updatedDeployments)));
            },
            cancellationToken);
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
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<DeleteWebServiceOutcome>, ITenantWriteOperation;


public sealed record DeleteWebServiceResponse(
    string Id,
    IReadOnlyList<PlacementSummaryDto> Deployments
);
