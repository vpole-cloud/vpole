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
                        IHttpProblemMapper httpProblems,
                        [AsParameters] GetWebServiceDeploymentHostnamesRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(outcome => outcome switch
                        {
                            IReadOnlyList<HostnameBindingSnapshot> value => Results.Ok(value),
                            GetWebServiceDeploymentHostnamesError error =>
                                httpProblems.Map(error).ToHttpResult()
                        }))
                .WithName("GetWebServiceDeploymentHostnames")
                .Produces<IReadOnlyList<HostnameBindingSnapshot>>();
            return app;
        }
    }
}

public union GetWebServiceDeploymentHostnamesError(
    WebServiceNotFound,
    WebServiceResourceDeploymentNotFound);

public union GetWebServiceDeploymentHostnamesOutcome(
    IReadOnlyList<HostnameBindingSnapshot>,
    GetWebServiceDeploymentHostnamesError);

public sealed record GetWebServiceDeploymentHostnamesRequest(
    [FromRoute] string Id,
    [FromRoute] string DeploymentId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<GetWebServiceDeploymentHostnamesOutcome>, ITenantReadOperation;

public sealed class GetWebServiceDeploymentHostnamesHandler(
    LoadWebServiceById loadWebService,
    LoadExactResourceDeployment loadExactResourceDeployment,
    LoadHostnameBindingsForDeployment loadHostnameBindingsForDeployment)
    : IMediatorOutcomeHandler<
        GetWebServiceDeploymentHostnamesRequest,
        GetWebServiceDeploymentHostnamesOutcome>
{
    public async Task<GetWebServiceDeploymentHostnamesOutcome> HandleAsync(
        GetWebServiceDeploymentHostnamesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await loadWebService(
                request.TenantId,
                request.Id,
                cancellationToken) is null)
        {
            return new GetWebServiceDeploymentHostnamesError(
                new WebServiceNotFound(request.Id));
        }

        var deployment = await loadExactResourceDeployment(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            request.DeploymentId,
            cancellationToken);
        if (deployment is null)
        {
            return new GetWebServiceDeploymentHostnamesError(
                new WebServiceResourceDeploymentNotFound(request.Id, request.DeploymentId));
        }

        var snapshots = await loadHostnameBindingsForDeployment(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            request.DeploymentId,
            cancellationToken);
        return new GetWebServiceDeploymentHostnamesOutcome(snapshots);
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
