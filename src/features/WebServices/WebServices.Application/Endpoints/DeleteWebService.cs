using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Events;
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
    IWebServiceDbContext dbContext,
    ISourceBuildReferenceLookup sourceBuildReferences,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
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

        var filter = Builders<WebService>.Filter.And(
            Builders<WebService>.Filter.ForTenant(request.TenantId),
            Builders<WebService>.Filter.Eq(ws => ws.Id, request.Id));
        var webService = await dbContext.WebServices
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);
        if (webService.Lifecycle is DeletingWebServiceLifecycle)
        {
            var currentPlacement = await deploymentQuery.GetAsync(
                request.TenantId,
                ResourceDeploymentResourceTypes.WebService,
                request.Id,
                cancellationToken);
            return currentPlacement is null
                ? Failures.Conflict(
                    "The deleting web service has no resource deployment.")
                : new DeleteWebServiceResponse(request.Id, currentPlacement);
        }

        return await quotaAdmissionExecutor.ExecuteAsync<WebServiceSpec, DeleteWebServiceResponse>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new DeleteQuotaMutation<WebServiceSpec>(webService.Spec),
            async (session, transactionCancellationToken) =>
            {
                var deleting = webService.BeginDeletion(
                    timeProvider.GetUtcNow().UtcDateTime);
                var replace = await dbContext.WebServices.ReplaceOneAsync(
                    session,
                    Builders<WebService>.Filter.And(
                        filter,
                        Builders<WebService>.Filter.Eq(
                            item => item.Revision,
                            webService.Revision)),
                    deleting,
                    cancellationToken: transactionCancellationToken);
                if (replace.ModifiedCount != 1)
                {
                    return Failures.Conflict(
                        "The web service changed while deletion was being accepted.");
                }

                var deploymentResult = await deploymentWriter.RequestDeletionAsync(
                    new ResourceDeploymentMutationCommand(
                        webService.TenantId,
                        ResourceDeploymentResourceTypes.WebService,
                        webService.Id),
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
    IWebServiceDbContext dbContext,
    IResourceDeploymentDbContext deploymentDbContext)
    : IConsumer<ResourceDeploymentDeletedEvent>
{
    public Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context)
    {
        var message = context.Message;
        if (!string.Equals(
                message.ResourceType,
                ResourceDeploymentResourceTypes.WebService.Value,
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var webServiceFilter = Builders<WebService>.Filter.And(
                Builders<WebService>.Filter.ForTenant(message.EventCore.TenantId),
                Builders<WebService>.Filter.Eq(
                    service => service.Id,
                    message.ResourceId),
                Builders<WebService>.Filter.OfType<
                    WebServiceLifecycle,
                    DeletingWebServiceLifecycle>(
                    service => service.Lifecycle));
            var webService = await dbContext.WebServices
                .Find(session, webServiceFilter)
                .FirstOrDefaultAsync(ct);
            if (webService is null)
                return Result.Success();

            var deployments = await deploymentDbContext.ResourceDeployments
                .Find(session, deployment =>
                    deployment.TenantId == message.EventCore.TenantId &&
                    deployment.ResourceType ==
                        ResourceDeploymentResourceTypes.WebService.Value &&
                    deployment.ResourceId == message.ResourceId)
                .ToListAsync(ct);
            if (deployments.Count == 0 ||
                deployments.Any(deployment =>
                    deployment.State is not DeletedResourceDeploymentState))
            {
                return Result.Success();
            }

            var deleted = await dbContext.WebServices.DeleteOneAsync(
                session,
                filter: webServiceFilter,
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
                return Result.Success();

            await deploymentDbContext.ResourceDeployments.DeleteManyAsync(
                session,
                filter: deployment =>
                    deployment.TenantId == message.EventCore.TenantId &&
                    deployment.ResourceType ==
                        ResourceDeploymentResourceTypes.WebService.Value &&
                    deployment.ResourceId == message.ResourceId,
                cancellationToken: ct);
            await dbContext.StoreEventAsync(
                webService.ToDeleteEvent(),
                session,
                ct);
            return Result.Success();
        }, cancellationToken: context.CancellationToken);
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
