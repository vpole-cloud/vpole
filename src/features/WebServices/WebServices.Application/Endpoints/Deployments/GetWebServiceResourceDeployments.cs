using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class GetWebServiceResourceDeployments
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGetWebServiceResourceDeploymentsEndpoint()
        {
            app.MapGet("/{Id}/deployments", async (IMediator mediator, [AsParameters] GetWebServiceResourceDeploymentsRequest request, CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(Results.Ok))
                .WithName("GetWebServiceResourceDeployments")
                .Produces<IReadOnlyList<PlacementSummaryDto>>();
            return app;
        }
    }
}

public sealed record GetWebServiceResourceDeploymentsRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyList<PlacementSummaryDto>>, ITenantReadOperation;

public sealed class GetWebServiceResourceDeploymentsHandler(
    LoadWebServiceById loadWebServiceById,
    LoadResourceDeploymentSet loadResourceDeploymentSet)
    : IMediatorHandler<GetWebServiceResourceDeploymentsRequest, IReadOnlyList<PlacementSummaryDto>>
{
    public async Task<Result<IReadOnlyList<PlacementSummaryDto>>> HandleAsync(
        GetWebServiceResourceDeploymentsRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);

        return Result.Success<IReadOnlyList<PlacementSummaryDto>>(await loadResourceDeploymentSet(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            cancellationToken));
    }
}
