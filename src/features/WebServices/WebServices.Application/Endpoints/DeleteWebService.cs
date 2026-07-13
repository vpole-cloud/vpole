using k8s;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Events;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.SourceBuilds.Contracts;
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
    ISourceBuildReferenceLookup sourceBuildReferences
    ) : IMediatorHandler<DeleteWebServiceRequest, DeleteWebServiceResponse>
{
    public async Task<Result<DeleteWebServiceResponse>> HandleAsync(DeleteWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = request.TenantId;
        if (tenantId is null)
        {
            var existing = await dbContext.WebServices
                .Find(service => service.Id == request.Id)
                .Project(service => service.TenantId)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is null)
                return Failures.NotFound("Web service not found.", request.Id);
            tenantId = existing;
        }

        var hasConfiguration = await sourceBuildReferences.HasConfigurationForWebServiceAsync(
            tenantId,
            request.Id,
            cancellationToken);
        if (!hasConfiguration.TryGetValue(out var exists, out var lookupFailure))
            return lookupFailure;
        if (exists)
        {
            return Failures.Conflict(
                "Delete the source build configuration before deleting this web service.");
        }

        return await dbContext.RunInTransactionAsync<DeleteWebServiceResponse>(async (sh, ct) =>
        {
            var filter = Builders<WebService>.Filter.And(
                Builders<WebService>.Filter.ForTenant(request.TenantId),
                Builders<WebService>.Filter.Eq(ws => ws.Id, request.Id));

            var webService = await dbContext.WebServices
                .FindOneAndDeleteAsync(sh, filter, cancellationToken: ct);

            if (webService is null)
                return Failures.NotFound("Web service not found.", request.Id);

            await dbContext.StoreEventAsync(webService.ToDeleteEvent(), sh, ct);
            return new DeleteWebServiceResponse(
                Id: request.Id
            );
        }, cancellationToken: cancellationToken);
    }
}

public class DeleteWebServiceFromKubernetesOnWebServiceDeletedConsumer(
    IKubernetes kubernetes
    ) : IConsumer<WebServiceDeletedEvent>
{
    public async Task Consume(ConsumeContext<WebServiceDeletedEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;
        
        await WebServiceKubernetesResourceManager.DeleteAsync(
            kubernetes,
            message.Name,
            message.Spec.KubernetesNamespace,
            ct);
    }
}

public sealed record DeleteWebServiceRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null
) : IMediatorRequest<DeleteWebServiceResponse>, ITenantWriteOperation;

public sealed record DeleteWebServiceResponse(
    string Id
);


