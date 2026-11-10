using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Queries;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;

public static class GetWebServiceDeploymentHostnames
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGetWebServiceDeploymentHostnamesEndpoint()
        {
            app.MapGet(
                    "/{Id}/deployments/{DeploymentId}/hostnames",
                    async (
                            IMediator mediator, [AsParameters] GetWebServiceDeploymentHostnamesRequest request,
                            CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                        {
                            IReadOnlyList<WebServiceHostnameBindingDto> value => Results.Ok(value),
                            GetWebServiceDeploymentHostnamesError error => MapError(error),
                        }))
                .WithName("GetWebServiceDeploymentHostnames")
                .Produces<IReadOnlyList<WebServiceHostnameBindingDto>>();
            return app;
        }
    }
}

public sealed record WebServiceHostnameBindingDto(
    string Id,
    string TenantId,
    string TenantDomainZoneId,
    string DeploymentTargetId,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string RelativeName,
    string Hostname,
    string Kind,
    DateTime CreatedAtUtc,
    string? TechnicalName,
    GitResourceStatus? GitOps
)
{
    public static WebServiceHostnameBindingDto
        FromSnapshot(HostnameBindingSnapshot binding, GitResourceStatus? status) => new(
        binding.Id,
        binding.TenantId,
        binding.TenantDomainZoneId,
        binding.DeploymentTargetId,
        binding.ResourceType,
        binding.ResourceId,
        binding.ResourceDeploymentId,
        binding.RelativeName,
        binding.Hostname,
        binding.Kind,
        binding.CreatedAtUtc,
        binding.TechnicalName,
        status);
}

public union GetWebServiceDeploymentHostnamesError(WebServiceNotFound, WebServiceResourceDeploymentNotFound);

public union GetWebServiceDeploymentHostnamesOutcome(
    IReadOnlyList<WebServiceHostnameBindingDto>,
    GetWebServiceDeploymentHostnamesError);

public sealed record GetWebServiceDeploymentHostnamesRequest(
    [FromRoute] string Id,
    [FromRoute] string DeploymentId,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetWebServiceDeploymentHostnamesOutcome>, ITenantReadOperation;

public sealed class GetWebServiceDeploymentHostnamesHandler(
    LoadWebServiceById loadWebService,
    LoadExactResourceDeployment loadExactResourceDeployment,
    LoadHostnameBindingsForDeployment loadHostnameBindingsForDeployment,
    IGitOpsMutations git
) : IMediatorOutcomeHandler<GetWebServiceDeploymentHostnamesRequest, GetWebServiceDeploymentHostnamesOutcome>
{
    public async Task<GetWebServiceDeploymentHostnamesOutcome> HandleAsync(
        GetWebServiceDeploymentHostnamesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await loadWebService(request.TenantId, request.Id, cancellationToken) is null)
            return new GetWebServiceDeploymentHostnamesError(new WebServiceNotFound(request.Id));

        var deployment = await loadExactResourceDeployment(
            new LoadExactResourceDeploymentRequest(
                TenantId: request.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: request.Id,
                DeploymentId: request.DeploymentId),
            cancellationToken: cancellationToken);
        if (deployment is null)
            return new GetWebServiceDeploymentHostnamesError(
                new WebServiceResourceDeploymentNotFound(request.Id, request.DeploymentId));

        var snapshots = await loadHostnameBindingsForDeployment(
            new LoadHostnameBindingsForDeploymentRequest(
                TenantId: request.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: request.Id,
                ResourceDeploymentId: request.DeploymentId),
            cancellationToken: cancellationToken);
        var statuses = await git.StatusesAsync(
            new LoadGitResourceStatusesRequest(
                request.TenantId,
                "HostnameBinding",
                snapshots.Select(item => item.Id).ToArray()),
            cancellationToken);
        return new GetWebServiceDeploymentHostnamesOutcome(
            snapshots.Select(item =>
                    WebServiceHostnameBindingDto.FromSnapshot(item, statuses.GetValueOrDefault(item.Id)))
                .ToArray());
    }
}

public sealed class GetWebServiceDeploymentHostnamesValidator
    : AbstractValidator<GetWebServiceDeploymentHostnamesRequest>
{
    public GetWebServiceDeploymentHostnamesValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Web service ID");
        RuleFor(request => request.DeploymentId).MongoObjectId("Resource deployment ID");
    }
}
