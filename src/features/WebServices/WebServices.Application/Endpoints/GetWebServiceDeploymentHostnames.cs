using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class GetWebServiceDeploymentHostnames
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGetWebServiceDeploymentHostnamesEndpoint()
        {
            app.MapGet(
                    "/{Id}/deployments/{DeploymentId}/hostnames",
                    async (
                        IMediator mediator,
                        [AsParameters] GetWebServiceDeploymentHostnamesRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(Results.Ok))
                .WithName("GetWebServiceDeploymentHostnames")
                .Produces<IReadOnlyList<HostnameBindingSnapshot>>();
            return app;
        }
    }
}

public sealed record GetWebServiceDeploymentHostnamesRequest(
    [FromRoute] string Id,
    [FromRoute] string DeploymentId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyList<HostnameBindingSnapshot>>, ITenantReadOperation;

public sealed class GetWebServiceDeploymentHostnamesHandler(
    LoadWebServiceById loadWebService,
    IResourceDeploymentSetQueryService deploymentQuery,
    IHostnameBindingCapability bindings)
    : IMediatorHandler<
        GetWebServiceDeploymentHostnamesRequest,
        IReadOnlyList<HostnameBindingSnapshot>>
{
    public async Task<Result<IReadOnlyList<HostnameBindingSnapshot>>> HandleAsync(
        GetWebServiceDeploymentHostnamesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await loadWebService(
                request.TenantId,
                request.Id,
                cancellationToken) is null)
        {
            return Failures.NotFound("Web service not found.", request.Id);
        }

        var deployment = await deploymentQuery.GetExactAsync(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            request.DeploymentId,
            cancellationToken);
        if (deployment is null)
        {
            return Failures.NotFound(
                "Resource deployment not found.",
                request.DeploymentId);
        }

        var snapshots = await bindings.ListForDeploymentAsync(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            request.DeploymentId,
            cancellationToken);
        return Result.Success<IReadOnlyList<HostnameBindingSnapshot>>(snapshots);
    }
}

public sealed class GetWebServiceDeploymentHostnamesValidator
    : AbstractValidator<GetWebServiceDeploymentHostnamesRequest>
{
    public GetWebServiceDeploymentHostnamesValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Web service ID");
        RuleFor(request => request.DeploymentId)
            .MongoObjectId("Resource deployment ID");
    }
}
