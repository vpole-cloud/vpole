using FluentValidation;
using k8s;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Server.Database;
using Pole.Server.Features.WebServices.Common;
using Pole.Server.Features.WebServices.Events;
using Pole.Server.Kuber;
using Pole.Server.Mediator;
using Pole.Server.ResultType;

namespace Pole.Server.Features.WebServices.Endpoints;

public static class Update
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
    [FromBody] UpdateWebServiceRequestBody Body
) : IMediatorRequest<UpdateWebServiceResponse>;

public sealed record UpdateWebServiceRequestBody(
    string ResourceGroupId,
    WebServiceSpecDto Spec
);

public sealed record UpdateWebServiceResponse(string Id);

public sealed class UpdateWebServiceHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<UpdateWebServiceRequest, UpdateWebServiceResponse>
{
    public async Task<Result<UpdateWebServiceResponse>> HandleAsync(
        UpdateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await dbContext.WebServices
            .Find(ws => ws.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (webService is null)
            return Result.NotFound($"Web service with ID '{request.Id}' not found.");

        return await dbContext.RunInTransactionAsync<UpdateWebServiceResponse>(async (sh, ct) =>
        {
            await dbContext.WebServices.UpdateOneAsync(
                session: sh,
                ws => ws.Id == request.Id,
                Builders<WebService>.Update
                    .Set(ws => ws.Spec, request.Body.Spec.ToDomain()),
                cancellationToken: ct);

            await dbContext.StoreEventAsync(new UpdateEvent
            {
                WebServiceId = request.Id
            }, sh, ct);
            
            return new UpdateWebServiceResponse(
                Id: webService.Id
            );
        }, cancellationToken: cancellationToken);
    }
}

public class UpdateWebServiceEventConsumer(
    MongoDbContext dbContext,
    IKubernetes kubernetes)
    : IConsumer<UpdateEvent>
{
    public async Task Consume(ConsumeContext<UpdateEvent> context)
    {
        var ct = context.CancellationToken;
        var webApp = await dbContext.WebServices
            .Find(wa => wa.Id == context.Message.WebServiceId)
            .FirstOrDefaultAsync(ct);
        if (webApp is null) return;

        var resourceGroup = await dbContext.ResourceGroups
            .Find(rg => rg.Id == webApp.ResourceGroupId)
            .FirstOrDefaultAsync(ct);
        if (resourceGroup is null) return;

        var inKuber = webApp.ToKubernetes();

        var configMap = inKuber.ToConfigMap();
        var deployment = inKuber.ToDeployment();
        var service = inKuber.ToService();
        var ingress = inKuber.ToIngress();
        
        await kubernetes.CoreV1.ReplaceNamespacedConfigMapAsync(configMap, configMap.Metadata.Name, webApp.Namespace, cancellationToken: ct);
        await kubernetes.AppsV1.ReplaceNamespacedDeploymentAsync(deployment, deployment.Metadata.Name, webApp.Namespace, cancellationToken: ct);
        await kubernetes.CoreV1.ReplaceNamespacedServiceAsync(service, service.Metadata.Name, webApp.Namespace, cancellationToken: ct);
        await kubernetes.NetworkingV1.ReplaceNamespacedIngressAsync(ingress, ingress.Metadata.Name, webApp.Namespace, cancellationToken: ct);
        
        await context.Publish(new UpdatedEvent(WebServiceId: webApp.Id), ct);
    }
}

public sealed class UpdateWebServiceValidator : AbstractValidator<UpdateWebServiceRequest>
{
    public UpdateWebServiceValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty().WithMessage("Web service ID is required.");

        RuleFor(x => x.Body.ResourceGroupId)
            .NotEmpty().WithMessage("Resource group ID is required.");

        RuleFor(x => x.Body.Spec)
            .NotNull().WithMessage("Web service specification is required.")
            .SetValidator(new WebServiceSpecDtoValidator());
    }
}