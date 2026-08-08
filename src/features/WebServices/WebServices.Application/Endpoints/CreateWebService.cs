using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.DnsRecords.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Application.Placement;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class CreateWebService
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
    PersistCreatedWebService persistCreatedWebService,
    PersistCurrentWebServiceRelease persistCurrentWebServiceRelease,
    IDomainNameReservationService domainNameReservationService,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    IResourceDeploymentPlacementResolver placementResolver,
    IResourceDeploymentWriter deploymentWriter)
    : IMediatorHandler<CreateWebServiceRequest, CreateWebServiceResponse>
{
    public async Task<Result<CreateWebServiceResponse>> HandleAsync(CreateWebServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var createWebServiceOptions = WebServiceFactory.DefaultOptions with
        {
            Tags = request.Body.Tags,
        };
        var deploymentId = ObjectId.GenerateNewId().ToString();
        var spec = request.Body.Spec.ToDomain();
        var targetResult = await placementResolver.ResolveTargetAsync(
            request.Body.Placement?.DeploymentTargetId,
            requiresIngress: true,
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;

        return await quotaAdmissionExecutor.ExecuteAsync<
            WebServiceSpec,
            WebServiceCreatePreparation,
            CreateWebServiceResponse>(
            request.TenantId,
            WebServiceQuotaResourceKey.Create(createWebServiceOptions.Id),
            new CreateQuotaMutation<WebServiceSpec>(spec),
            transactionCancellationToken => PrepareAsync(
                request,
                createWebServiceOptions.Id,
                target,
                transactionCancellationToken),
            async (preparation, session, transactionCancellationToken) =>
            {
                var newWebService = WebServiceFactory.Create(
                    name: request.Body.Name,
                    tenantId: request.TenantId,
                    resourceGroupId: request.Body.ResourceGroupId,
                    spec: spec,
                    dnsRecord: preparation.DnsRecord,
                    options: createWebServiceOptions);
                var deploymentEventCore = EventCore.Create(request.TenantId);
                await persistCreatedWebService(
                    session,
                    newWebService,
                    transactionCancellationToken);
                var deploymentResult = await deploymentWriter.CreateAsync(
                    new CreateResourceDeploymentCommand(
                        deploymentId,
                        request.TenantId,
                        WebServiceResourceDeployment.ResourceType,
                        newWebService.Id,
                        newWebService.ResourceGroupId,
                        request.Body.Placement?.IsolationScopeId,
                        preparation.Target,
                        Storage: null,
                        ResourcePlacementCapability.SingleTarget),
                    session,
                    transactionCancellationToken);
                if (!deploymentResult.TryGetValue(
                        out var deployment,
                        out var deploymentFailure))
                {
                    return deploymentFailure;
                }

                var release = WebServiceReleaseFactory.CreatePending(
                    request.TenantId,
                    newWebService.Id,
                    newWebService.ActiveRevision,
                    WebServiceReleaseAction.Create,
                    request.ActorUserId.Value,
                    request.ActorEmail.Value,
                    newWebService.Spec.ToSnapshot(),
                    deploymentEventCore.TraceId,
                    deploymentEventCore.CorrelationId,
                    deploymentEventCore.OccurredOn,
                    deploymentGeneration: deployment.DesiredGeneration);
                await persistCurrentWebServiceRelease(
                    session,
                    release,
                    transactionCancellationToken);

                return new CreateWebServiceResponse(
                    newWebService.Id,
                    deployment.ToPlacementSummaryDto());
            },
            cancellationToken);
    }

    private async Task<Result<WebServiceCreatePreparation>> PrepareAsync(
        CreateWebServiceRequest request,
        string webServiceId,
        ResolvedDeploymentTarget target,
        CancellationToken cancellationToken)
    {
        var dnsResult = await domainNameReservationService.ReserveAsync(
            new ReserveDomainNameRequest(
                TenantId: request.TenantId,
                DomainName: request.Body.DomainName,
                ResourceType: ResourceType.WebService,
                ResourceId: webServiceId,
                ResourceName: request.Body.Name,
                ResourceGroupId: request.Body.ResourceGroupId),
            cancellationToken);
        return dnsResult.TryGetValue(out var dnsRecord, out var dnsFailure)
            ? new WebServiceCreatePreparation(dnsRecord, target)
            : dnsFailure;
    }

    private sealed record WebServiceCreatePreparation(
        DnsRecordShortInfo DnsRecord,
        ResolvedDeploymentTarget Target);
}

public sealed record CreateWebServiceRequest(
    [FromBody] CreateWebServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string> ActorUserId,
    [FromClaim(Claims.Email)] ClaimValue<string> ActorEmail
) : IMediatorRequest<CreateWebServiceResponse>, ITenantCreateOperation;

public sealed record CreateWebServiceRequestBody(
    string Name,
    string ResourceGroupId,
    WebServiceSpecDto Spec,
    string DomainName,
    ImmutableHashSet<string> Tags,
    PlacementRequest? Placement = null);
public sealed record CreateWebServiceResponse(
    string WebServiceId,
    PlacementSummaryDto Placement);


public sealed class CreateWebServiceRequestValidator : AbstractValidator<CreateWebServiceRequest>
{
    public CreateWebServiceRequestValidator()
    {
        RuleFor(x => x.ActorUserId.Value).NotEmpty();
        RuleFor(x => x.ActorEmail.Value).NotEmpty().EmailAddress();
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
        When(body => body.Placement is not null, () =>
        {
            RuleFor(body => body.Placement!.IsolationScopeId)
                .OptionalMongoObjectId("Isolation scope ID");
            RuleFor(body => body.Placement!.DeploymentTargetId)
                .OptionalMongoObjectId("Deployment target ID");
        });
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
