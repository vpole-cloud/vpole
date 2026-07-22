using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Events;
using Pole.Features.WebServices.Application.Kubernetes;
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
                return result.ToHttpResult(x => Results.Accepted(value: x.Id));
            })
            .WithName("DeleteWebService")
            .Produces(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed class DeleteWebServiceHandler(
    IWebServiceDbContext dbContext,
    ISourceBuildReferenceLookup sourceBuildReferences,
    IQuotaAdmissionExecutor quotaAdmissionExecutor
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

        return await quotaAdmissionExecutor.ExecuteAsync<WebServiceSpec, DeleteWebServiceResponse>(
            webService.TenantId,
            WebServiceQuotaResourceKey.Create(webService.Id),
            new DeleteQuotaMutation<WebServiceSpec>(webService.Spec),
            async (session, transactionCancellationToken) =>
            {
                var deleted = await dbContext.WebServices.FindOneAndDeleteAsync(
                    session,
                    filter,
                    cancellationToken: transactionCancellationToken);
                if (deleted is null)
                    return Failures.NotFound("Web service not found.", request.Id);

                await dbContext.StoreEventAsync(
                    deleted.ToDeleteEvent(),
                    session,
                    transactionCancellationToken);
                return new DeleteWebServiceResponse(request.Id);
            },
            cancellationToken);
    }
}

public class DeleteWebServiceFromKubernetesOnWebServiceDeletedConsumer(
    IKubernetesClientProvider kubernetesClientProvider
    ) : IConsumer<WebServiceDeletedEvent>
{
    public async Task Consume(ConsumeContext<WebServiceDeletedEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;
        var kubernetes = await kubernetesClientProvider.GetDefaultAsync(ct);
        
        await WebServiceKubernetesResourceManager.DeleteAsync(
            kubernetes,
            message.Name,
            message.Spec.KubernetesNamespace,
            ct);
    }
}

public sealed record DeleteWebServiceRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<DeleteWebServiceResponse>, ITenantWriteOperation;

public sealed record DeleteWebServiceResponse(
    string Id
);


