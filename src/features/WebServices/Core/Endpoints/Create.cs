using System.Collections.Immutable;
using FluentValidation;
using k8s;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Feature.DnsRecords.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Common;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Features.WebServices.Kubernetes;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Endpoints;

public static class Create
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapCreateWebServiceEndpoint()
        {
            app.MapPost("/", async (IMediator mediator, [AsParameters] CreateWebServiceRequest request, CancellationToken cancellationToken) =>
                {
                    var response = await mediator.Send(request, cancellationToken);
                    return response.ToHttpResult(v => Results.Accepted(value: v));
                })
                .WithName("CreateWebService")
                .Produces<CreateWebServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed class CreateWebServiceHandler(
    IWebServiceDbContext dbContext)
    : IMediatorHandler<CreateWebServiceRequest, CreateWebServiceResponse>
{
    public async Task<Result<CreateWebServiceResponse>> HandleAsync(CreateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var group = await dbContext.ResourceGroups
            .Find(rg => rg.Id == request.Body.ResourceGroupId)
            .FirstOrDefaultAsync(cancellationToken);
        if (group is null) return Result.NotFound($"Resource group with ID '{request.Body.ResourceGroupId}' not found.");
        
        var dnsRecord = await dbContext.DnsRecords
            .Find(dn => dn.Domain == request.Body.DomainName)
            .FirstOrDefaultAsync(cancellationToken);
        if (dnsRecord is not null)
            return Result.Conflict($"Domain name '{dnsRecord.Domain}' is already in use.");

        var (newWebService, newDnsRecord) = BuildWebServiceWithDnsRecord(request, group);
        
        return await dbContext.RunInTransactionAsync<CreateWebServiceResponse>(async (sh, ct) =>
        {
            await dbContext.WebServices.InsertOneAsync(sh, newWebService, cancellationToken: ct);

            await dbContext.DnsRecords.InsertOneAsync(sh, newDnsRecord, cancellationToken: ct);
            
            await dbContext.StoreEventAsync(newWebService.ToCreateEvent(), sh, ct);

            return new CreateWebServiceResponse(newWebService.Id);
        }, cancellationToken: cancellationToken);
    }

    
    private (WebService, DnsRecord) BuildWebServiceWithDnsRecord(CreateWebServiceRequest request, ResourceGroup group)
    {
        var createWebServiceOptions = WebServiceFactory.DefaultOptions with
        {
            Tags = request.Body.Tags,
        };
        var dnsRecord = DnsRecordFactory.Create(
            name: request.Body.DomainName,
            resourceType: ResourceType.WebService,
            resourceId: createWebServiceOptions.Id,
            resourceName: request.Body.Name,
            groupId: request.Body.ResourceGroupId,
            options: DnsRecordFactory.DefaultCreateOptions
        );
            
        var newWebService = WebServiceFactory.Create(
            name: request.Body.Name,
            resourceGroupId: request.Body.ResourceGroupId,
            spec: request.Body.Spec.ToDomain(group.KubernetesNamespace),
            dnsRecord: new DnsRecordShortInfo(Id: dnsRecord.Id, Domain: dnsRecord.Domain),
            options: createWebServiceOptions
        );
        return (newWebService, dnsRecord);
    }
}

public class CreateWebServiceEventConsumer(
    IKubernetes kubernetes)
    : IConsumer<CreateEvent>
{
    public async Task Consume(ConsumeContext<CreateEvent> context)
    {
        var ct = context.CancellationToken;
        
        await WebServiceKubernetesResourceManager.CreateAsync(kubernetes, 
            context.Message.ToWebService().ToKubernetes() , ct);
    }
}

public sealed record CreateWebServiceRequest(
    [FromBody] CreateWebServiceRequestBody Body
) : IMediatorRequest<CreateWebServiceResponse>;

public sealed record CreateWebServiceRequestBody(
    string Name,
    string ResourceGroupId,
    WebServiceSpecDto Spec,
    string DomainName,
    ImmutableHashSet<string> Tags);
public sealed record CreateWebServiceResponse(
    string WebServiceId);


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
        RuleFor(x => x.DomainName)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public sealed class DnsRecordDtoValidator : AbstractValidator<DnsRecordShortInfoDto>
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
        RuleFor(x => x.Resources)
            .SetValidator(new ResourcesValidator());
        RuleFor(x => x.Probes)
            .SetValidator(new ProbeSpecDtoValidator());
        RuleFor(x => x.Config)
            .SetValidator(new WebServiceConfigDtoValidator());
    }
}

public class ProbeSpecDtoValidator : AbstractValidator<ProbeSpecDto>
{
    public ProbeSpecDtoValidator()
    {
        RuleFor(x => x.Startup)
            .SetValidator(new ProbeConfigDtoValidator());
        RuleFor(x => x.Liveness)
            .SetValidator(new ProbeConfigDtoValidator());
        RuleFor(x => x.Readiness)
            .SetValidator(new ProbeConfigDtoValidator());
    }
}

public class ProbeConfigDtoValidator : AbstractValidator<ProbeConfigDto>
{
    public ProbeConfigDtoValidator()
    {
        RuleFor(x => x.Path)
            .NotEmpty()
            .MaximumLength(200);
        RuleFor(x => x.Port)
            .NotEmpty()
            .MaximumLength(10);
        RuleFor(x => x.InitialDelaySeconds)
            .GreaterThanOrEqualTo(0)
            .LessThanOrEqualTo(300);
        RuleFor(x => x.PeriodSeconds)
            .GreaterThan(0)
            .LessThanOrEqualTo(120);
        RuleFor(x => x.TimeoutSeconds)
            .GreaterThan(0)
            .LessThanOrEqualTo(60);
        RuleFor(x => x.FailureThreshold)
            .GreaterThan(0)
            .LessThanOrEqualTo(10);
        RuleFor(x => x.SuccessThreshold)
            .GreaterThan(0)
            .LessThanOrEqualTo(10);
                
    }
}

public class ResourcesValidator : AbstractValidator<Resources>
{
    public ResourcesValidator()
    {
        RuleFor(x => x.Requests)
            .SetValidator(new ResourceValidator());
        RuleFor(x => x.Limits)
            .SetValidator(new ResourceValidator());
    }
}

public class ResourceValidator : AbstractValidator<Resource>
{
    public ResourceValidator()
    {
        RuleFor(x => x.CpuCores)
            .GreaterThan(0);
        RuleFor(x => x.MemoryMi)
            .GreaterThan(0);
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
            .NotEmpty()
            .MaximumLength(10);
        RuleFor(x => x.Protocol)
            .NotEmpty()
            .Must(p => p is "TCP" or "UDP" or "SCTP")
            .WithMessage("Protocol must be either 'TCP' or 'UDP'.");
    }
}
