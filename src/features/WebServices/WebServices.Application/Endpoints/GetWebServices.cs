using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class GetWebServices
{

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGetWebServicesEndpoint()
        {
            app.MapGet("/", async (IMediator mediator, [AsParameters] GetWebServicesRequest request) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetWebServices")
            .WithSummary("Retrieves all web services.")
            .WithDescription("Fetches a list of all web services available in the system.")
            .Produces<List<WebServiceDto>>();

            return app;
        }
    }
}

public sealed class GetWebServicesHandler(
    LoadWebServicePage loadWebServicePage,
    LoadResourceDeploymentSets loadResourceDeploymentSets)
    : IMediatorOutcomeHandler<GetWebServicesRequest, PaginatedResponse<WebServiceDto>>
{
    public async Task<PaginatedResponse<WebServiceDto>> HandleAsync(
        GetWebServicesRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await loadWebServicePage(
            new WebServiceListQuery(
                request.TenantId,
                request.Search,
                request.ResourceGroupId,
                request.PageNumber,
                request.PageSize),
            cancellationToken);
        var placements = await loadResourceDeploymentSets(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            page.Items.Select(service => service.Id).ToArray(),
            cancellationToken);

        return new PaginatedResponse<WebServiceDto>(
            Items: page.Items.Select(service => service.ToDto(
                placements.GetValueOrDefault(service.Id))),
            TotalCount: page.TotalCount,
            PageNumber: request.PageNumber,
            PageSize: request.PageSize);
    }
}

public record GetWebServicesRequest : PaginatedRequest<WebServiceDto>, ITenantReadOperation
{
    [FromHeader(Name = TenantHeaders.TenantId)] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public string? ResourceGroupId { get; init; }
    [FromQuery] public string? Search { get; init; }
}
