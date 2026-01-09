using k8s;
using MassTransit;
using MongoDB.Driver;
using Pole.Server.Database;
using Pole.Server.Events.WebServices;
using Pole.Server.Extensions;
using Pole.Server.Mediator;
using Pole.Server.ResultType;

namespace Pole.Server.Api.WebServices.Endpoints;

public static class DeleteWebServiceEndpoints
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
    MongoDbContext mongoDbContext,
    IPublishEndpoint publishEndpoint
    ) : IMediatorHandler<DeleteWebServiceRequest, DeleteWebServiceResponse>
{
    public async Task<Result<DeleteWebServiceResponse>> HandleAsync(DeleteWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await mongoDbContext.WebServices
            .Find(ws => ws.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (webService is null)
            return Result.NotFound($"Web service with ID '{request.Id}' not found.");
        
        await publishEndpoint.Publish(new DeleteWebServiceRequestedEvent(request.Id, webService.ResourceGroupId), cancellationToken);

        return new DeleteWebServiceResponse(
            Id: request.Id
        );
    }
}

public class DeleteWebServiceRequestedEventConsumer(
    MongoDbContext mongoDbContext,
    IKubernetes kubernetes
    ) : IConsumer<DeleteWebServiceRequestedEvent>
{
    public async Task Consume(ConsumeContext<DeleteWebServiceRequestedEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;
        
        var webService = await mongoDbContext.WebServices
            .Find(ws => ws.Id == message.WebServiceId)
            .FirstOrDefaultAsync(ct);
        if (webService is null)
            return;

        var inKuber = webService.ToKubernetes();
        
        await kubernetes.AppsV1.DeleteNamespacedDeploymentAsync(
            name: inKuber.DeploymentName,
            namespaceParameter: message.ResourceGroupId,
            cancellationToken: ct);
        
        await kubernetes.CoreV1.DeleteNamespacedServiceAsync(
            name: inKuber.ServiceName,
            namespaceParameter: message.ResourceGroupId,
            cancellationToken: ct);
        
        await kubernetes.CoreV1.DeleteNamespacedSecretAsync(
            name: inKuber.TlsSecretName,
            namespaceParameter: message.ResourceGroupId,
            cancellationToken: ct);
        
        await kubernetes.NetworkingV1.DeleteNamespacedIngressAsync(
            name: message.WebServiceId,
            namespaceParameter: message.ResourceGroupId,
            cancellationToken: ct);
        
        await kubernetes.CoreV1.DeleteNamespacedConfigMapAsync(
            name: inKuber.ConfigMapName,
            namespaceParameter: message.ResourceGroupId,
            cancellationToken: ct);

        await mongoDbContext.WebServices
            .DeleteOneAsync(ws => ws.Id == message.WebServiceId, cancellationToken: ct);
    }
}

public sealed record DeleteWebServiceRequest(
    string Id
    ) : IMediatorRequest<DeleteWebServiceResponse>;

public sealed record DeleteWebServiceResponse(
    string Id
    );