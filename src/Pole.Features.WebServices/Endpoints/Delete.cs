using k8s;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Events.WebServices;
using Pole.Features.WebServices.Kubernetes;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Endpoints;

public static class Delete
{
    public const string Route = "/{Id}";
    public static IEndpointRouteBuilder MapDeleteWebServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/", async (IMediator mediator, [AsParameters] DeleteWebServiceRequest request, CancellationToken ct) =>
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
        var webService = await dbContext.WebServices
            .Find(ws => ws.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (webService is null)
            return Result.NotFound($"Web service with ID '{request.Id}' not found.");
        
        return await dbContext.RunInTransactionAsync<DeleteWebServiceResponse>(async (sh, ct) =>
        {
            await dbContext.WebServices
                .DeleteOneAsync(sh, ws => ws.Id == request.Id, cancellationToken: ct);

            await dbContext.DnsRecords
                .DeleteOneAsync(sh, record => record.ResourceId == request.Id, cancellationToken: ct);
        
            await dbContext.StoreEventAsync(new DeleteEvent(webService.Id, webService.Name, webService.ResourceGroupId), sh, ct);
            return new DeleteWebServiceResponse(
                Id: request.Id
            );
        }, cancellationToken: cancellationToken);
    }
}

public class DeleteWebServiceRequestedEventConsumer(
    MongoDbContext dbContext,
    IKubernetes kubernetes
    ) : IConsumer<DeleteEvent>
{
    public async Task Consume(ConsumeContext<DeleteEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;
        
        var resourceGroup = await dbContext.ResourceGroups
            .Find(rg => rg.Id == message.ResourceGroupId)
            .FirstOrDefaultAsync(ct);
        if (resourceGroup is null)
            return;
        var ns = resourceGroup.NamespaceName;
        
        await kubernetes.AppsV1.DeleteNamespacedDeploymentAsync(
            name: WebServiceInKubernetes.BuildDeploymentName(message.WebServiceId),
            namespaceParameter: ns,
            cancellationToken: ct);
        
        await kubernetes.CoreV1.DeleteNamespacedServiceAsync(
            name: WebServiceInKubernetes.BuildServiceName(message.WebServiceId),
            namespaceParameter: ns,
            cancellationToken: ct);
        
        await kubernetes.CoreV1.DeleteNamespacedSecretAsync(
            name: WebServiceInKubernetes.BuildTlsSecretName(message.WebServiceId),
            namespaceParameter: ns,
            cancellationToken: ct);
        
        await kubernetes.NetworkingV1.DeleteNamespacedIngressAsync(
            name: WebServiceInKubernetes.BuildIngressName(message.WebServiceId),
            namespaceParameter: ns,
            cancellationToken: ct);
        
        await kubernetes.CoreV1.DeleteNamespacedConfigMapAsync(
            name: WebServiceInKubernetes.BuildConfigMapName(message.WebServiceId),
            namespaceParameter: ns,
            cancellationToken: ct);
    }
}

public sealed record DeleteWebServiceRequest(
    string Id
) : IMediatorRequest<DeleteWebServiceResponse>;

public sealed record DeleteWebServiceResponse(
    string Id
);
