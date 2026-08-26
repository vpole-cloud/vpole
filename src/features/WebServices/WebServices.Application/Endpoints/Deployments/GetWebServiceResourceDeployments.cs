using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Mediator;

namespace Pole.Features.WebServices.Application.Endpoints.Deployments;

public static class GetWebServiceResourceDeployments
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGetWebServiceResourceDeploymentsEndpoint()
        {
            app.MapGet("/{Id}/deployments", async (
                    IMediator mediator,
                    IHttpProblemMapper httpProblems,
                    [AsParameters] GetWebServiceResourceDeploymentsRequest request,
                    CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(
                    outcome => outcome switch
                    {
                        IReadOnlyList<PlacementSummaryDto> value => Results.Ok(value),
                        WebServiceNotFound error => httpProblems.Map(error).ToHttpResult()
                    }))
                .WithName("GetWebServiceResourceDeployments")
                .Produces<IReadOnlyList<PlacementSummaryDto>>();
            return app;
        }
    }
}

public union GetWebServiceResourceDeploymentsOutcome(
    IReadOnlyList<PlacementSummaryDto>,
    WebServiceNotFound);

public sealed record GetWebServiceResourceDeploymentsRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<GetWebServiceResourceDeploymentsOutcome>, ITenantReadOperation;

public sealed class GetWebServiceResourceDeploymentsHandler(
    LoadWebServiceById loadWebServiceById,
    LoadResourceDeploymentSet loadResourceDeploymentSet)
    : IMediatorOutcomeHandler<
        GetWebServiceResourceDeploymentsRequest,
        GetWebServiceResourceDeploymentsOutcome>
{
    public async Task<GetWebServiceResourceDeploymentsOutcome> HandleAsync(
        GetWebServiceResourceDeploymentsRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return new WebServiceNotFound(request.Id);

        return new GetWebServiceResourceDeploymentsOutcome(await loadResourceDeploymentSet(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            cancellationToken));
    }
}
