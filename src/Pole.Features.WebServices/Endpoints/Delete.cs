using k8s;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.WebServices.Common;
using Pole.Features.WebServices.Events;
using Pole.Features.WebServices.Kubernetes;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Endpoints;

public static class Delete
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
    MongoDbContext dbContext
    ) : IMediatorHandler<DeleteWebServiceRequest, DeleteWebServiceResponse>
{
    public async Task<Result<DeleteWebServiceResponse>> HandleAsync(DeleteWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.RunInTransactionAsync<DeleteWebServiceResponse>(async (sh, ct) =>
        {
            var webService = await dbContext.WebServices
                .FindOneAndDeleteAsync(sh, ws => ws.Id == request.Id, cancellationToken: ct);

            if (webService is null)
                return Result.NotFound($"Web service with ID '{request.Id}' not found.");
            
            await dbContext.DnsRecords
                .DeleteOneAsync(sh, record => record.Id == webService.DnsRecord.Id, cancellationToken: ct);
            
            await dbContext.StoreEventAsync(webService.ToDeleteEvent(), sh, ct);
            return new DeleteWebServiceResponse(
                Id: request.Id
            );
        }, cancellationToken: cancellationToken);
    }
}

public class DeleteWebServiceRequestedEventConsumer(
    IKubernetes kubernetes
    ) : IConsumer<DeleteEvent>
{
    public async Task Consume(ConsumeContext<DeleteEvent> context)
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
    [FromRoute] string Id
) : IMediatorRequest<DeleteWebServiceResponse>;

public sealed record DeleteWebServiceResponse(
    string Id
);
