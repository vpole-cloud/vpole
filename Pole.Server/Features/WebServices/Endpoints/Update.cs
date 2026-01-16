using FluentValidation;
using k8s;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Server.Database;
using Pole.Server.Extensions;
using Pole.Server.Features.WebServices.Common;
using Pole.Server.Features.WebServices.Events;
using Pole.Server.Mediator;
using Pole.Server.ResultType;

namespace Pole.Server.Features.WebServices.Endpoints;

public static class Update
{
    public const string Route = "/{Id}";
    
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapUpdateWebServiceEndpoint()
        {
            app.MapPut(Route, async (IMediator mediator, UpdateWebServiceRequest request, CancellationToken cancellationToken) =>
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
    WebServiceSpecDto Spec,
    DnsRecordDto DnsRecord);

public sealed record UpdateWebServiceResponse(string Id);

public sealed class UpdateWebServiceHandler(
    IPublishEndpoint publishEndpoint,
    MongoDbContext mongoDbContext)
    : IMediatorHandler<UpdateWebServiceRequest, UpdateWebServiceResponse>
{
    public async Task<Result<UpdateWebServiceResponse>> HandleAsync(
        UpdateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await mongoDbContext.WebServices
            .Find(ws => ws.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (webService is null)
            return Result.NotFound($"Web service with ID '{request.Id}' not found.");

        webService = webService with
        {
            Spec = request.Body.Spec.ToDomain(),
            DnsRecord = request.Body.DnsRecord.ToDomain()
        };

        await mongoDbContext.WebServices.ReplaceOneAsync(
            ws => ws.Id == request.Id,
            webService,
            cancellationToken: cancellationToken);

        await publishEndpoint.Publish(new UpdateEvent
        {
            WebServiceId = request.Id
        }, cancellationToken);

        return new UpdateWebServiceResponse(
            Id: webService.Id
            );
    }
}

public class UpdateWebServiceEventConsumer(
    MongoDbContext mongoDbContext,
    IKubernetes kubernetes)
    : IConsumer<UpdateEvent>
{
    public async Task Consume(ConsumeContext<UpdateEvent> context)
    {
        var ct = context.CancellationToken;
        var webApp = await mongoDbContext.WebServices
            .Find(wa => wa.Id == context.Message.WebServiceId)
            .FirstOrDefaultAsync(ct);
        if (webApp is null) return;

        var resourceGroup = await mongoDbContext.ResourceGroups
            .Find(rg => rg.Id == webApp.ResourceGroupId)
            .FirstOrDefaultAsync(ct);
        if (resourceGroup is null) return;

        var ns = resourceGroup.NamespaceName;
        var inKuber = webApp.ToKubernetes();

        var configMap = inKuber.ToConfigMap();
        var deployment = inKuber.ToDeployment();
        var service = inKuber.ToService();
        var ingress = inKuber.ToIngress();
        
        await kubernetes.CoreV1.ReplaceNamespacedConfigMapAsync(configMap, configMap.Metadata.Name, ns, cancellationToken: ct);
        await kubernetes.AppsV1.ReplaceNamespacedDeploymentAsync(deployment, deployment.Metadata.Name, ns, cancellationToken: ct);
        await kubernetes.CoreV1.ReplaceNamespacedServiceAsync(service, service.Metadata.Name, ns, cancellationToken: ct);
        await kubernetes.NetworkingV1.ReplaceNamespacedIngressAsync(ingress, ingress.Metadata.Name, ns, cancellationToken: ct);
        
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
        
        RuleFor(x => x.Body.DnsRecord)
            .NotNull().WithMessage("DNS record is required.")
            .SetValidator(new DnsRecordDtoValidator());
    }
}