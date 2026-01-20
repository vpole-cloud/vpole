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

public static class Create
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapCreateWebServiceEndpoint()
        {
            app.MapPost("/", async (IMediator mediator, [AsParameters] CreateWebServiceRequest request) =>
                {
                    var response = await mediator.Send(request);
                    return response.ToHttpResult(v => Results.Accepted(value: v));
                })
                .WithName("CreateWebService")
                .Produces<CreateWebServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed class CreateWebServiceHandler(
    IPublishEndpoint publishEndpoint,
    IKubernetes kubernetes,
    MongoDbContext mongoDbContext)
    : IMediatorHandler<CreateWebServiceRequest, CreateWebServiceResponse>
{
    public async Task<Result<CreateWebServiceResponse>> HandleAsync(CreateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var group = await mongoDbContext.ResourceGroups
            .Find(rg => rg.Id == request.Body.ResourceGroupId)
            .FirstOrDefaultAsync(cancellationToken);
        if (group is null) return Result.Failure($"Resource group with ID '{request.Body.ResourceGroupId}' not found.");
        
        var newWebService = new WebService(
            Name: request.Body.Name,
            ResourceGroupId: request.Body.ResourceGroupId,
            Spec: request.Body.Spec.ToDomain(),
            DnsRecord: request.Body.DnsRecord.ToDomain()
        );
        // var pullSecret = newWebService.ToImagePullSecret();
        // if (request.Body.Spec.Image.SecretName is not null)
        //     await K8sCreateOnly.CreateOrIgnoreAsync(() =>
        //         kubernetes.CoreV1.CreateNamespacedSecretAsync(pullSecret, group.NamespaceName, cancellationToken: cancellationToken));

        await mongoDbContext.WebServices.InsertOneAsync(newWebService, cancellationToken: cancellationToken);

        await publishEndpoint.Publish(new CreateEvent
        {
            WebServiceId = newWebService.Id,
        }, cancellationToken);

        return new CreateWebServiceResponse(
            WebServiceId: newWebService.Id
        );
    }
}

public class CreateWebServiceEventConsumer(
    MongoDbContext mongoDbContext,
    IKubernetes kubernetes)
    : IConsumer<CreateEvent>
{
    public async Task Consume(ConsumeContext<CreateEvent> context)
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

        await KubernetesExtensions.CreateOrIgnoreAsync(() =>
            kubernetes.CoreV1.CreateNamespacedConfigMapAsync(configMap, ns, cancellationToken: ct));

        await KubernetesExtensions.CreateOrIgnoreAsync(() =>
            kubernetes.AppsV1.CreateNamespacedDeploymentAsync(deployment, ns, cancellationToken: ct));

        await KubernetesExtensions.CreateOrIgnoreAsync(() =>
            kubernetes.CoreV1.CreateNamespacedServiceAsync(service, ns, cancellationToken: ct));

        await KubernetesExtensions.CreateOrIgnoreAsync(() =>
            kubernetes.NetworkingV1.CreateNamespacedIngressAsync(ingress, ns, cancellationToken: ct));
    }
}

public sealed record CreateWebServiceRequest(
    [FromBody] CreateWebServiceRequestBody Body
) : IMediatorRequest<CreateWebServiceResponse>;

public sealed record CreateWebServiceRequestBody(
    string Name,
    string ResourceGroupId,
    WebServiceSpecDto Spec,
    DnsRecordDto DnsRecord);

public sealed record CreateWebServiceResponse(
    string WebServiceId
);


public sealed class CreateWebServiceRequestValidator : AbstractValidator<CreateWebServiceRequest>
{
    public CreateWebServiceRequestValidator()
    {
        RuleFor(x => x.Body).SetValidator(new CreateWebServiceRequestBodyValidator());
    }
}

public sealed class CreateWebServiceRequestBodyValidator : AbstractValidator<CreateWebServiceRequestBody>
{
    public CreateWebServiceRequestBodyValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(30);
        RuleFor(x => x.ResourceGroupId)
            .NotEmpty();
        RuleFor(x => x.Spec)
            .SetValidator(new WebServiceSpecDtoValidator());
        RuleFor(x => x.DnsRecord)
            .SetValidator(new DnsRecordDtoValidator());
    }
}

public sealed class DnsRecordDtoValidator : AbstractValidator<DnsRecordDto>
{
    public DnsRecordDtoValidator()
    {
        RuleFor(x => x.Domain)
            .NotEmpty()
            .MaximumLength(253);
    }
}

public sealed class WebServiceSpecDtoValidator : AbstractValidator<WebServiceSpecDto>
{
    public WebServiceSpecDtoValidator()
    {
        RuleFor(x => x.Image)
            .SetValidator(new ImageInfoDtoValidator());
        RuleFor(x => x.Replicas)
            .GreaterThan(0);
        RuleFor(x => x.CpuCores)
            .GreaterThan(0);
        RuleFor(x => x.MemoryMi)
            .GreaterThan(0);
        RuleFor(x => x.Config)
            .SetValidator(new WebServiceConfigDtoValidator());
    }
}

public sealed class ImageInfoDtoValidator : AbstractValidator<ImageInfoDto>
{
    public ImageInfoDtoValidator()
    {
        RuleFor(x => x.Server)
            .NotEmpty()
            .MaximumLength(255);
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(255);
        RuleFor(x => x.Tag)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public sealed class WebServiceConfigDtoValidator : AbstractValidator<WebServiceConfigDto>
{
    public WebServiceConfigDtoValidator()
    {
        RuleForEach(x => x.EnvironmentVariables)
            .Must(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .WithMessage("Environment variable keys must not be empty.")
            .Must(kv => kv.Key.Length <= 100)
            .WithMessage("Environment variable keys must not exceed 100 characters.")
            .Must(kv => kv.Value.Length <= 1000)
            .WithMessage("Environment variable values must not exceed 1000 characters.");

        RuleForEach(x => x.PortMappings)
            .SetValidator(new PortMappingDtoValidator());
    }
}

public sealed class PortMappingDtoValidator : AbstractValidator<PortMappingDto>
{
    public PortMappingDtoValidator()
    {
        RuleFor(x => x.ServicePort)
            .InclusiveBetween(1, 65535);
        RuleFor(x => x.TargetPort)
            .InclusiveBetween(1, 65535);
        RuleFor(x => x.Protocol)
            .NotEmpty()
            .Must(p => p is "TCP" or "UDP")
            .WithMessage("Protocol must be either 'TCP' or 'UDP'.");
    }
}