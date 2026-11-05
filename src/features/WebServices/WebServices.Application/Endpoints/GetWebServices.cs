using Pole.Features.GitOps.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Connections;
using Pole.Features.WebServices.Application.Queries;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class GetWebServices
{

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGetWebServicesEndpoint()
        {
            app.MapGet("/", async (
                IMediator mediator,
                [AsParameters] GetWebServicesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    PaginatedResponse<WebServiceDto> page => Results.Ok(page)
                });
            })
            .WithName("GetWebServices")
            .WithSummary("Retrieves all web services.")
            .WithDescription("Fetches a paginated list of web services in the selected tenant.")
            .Produces<PaginatedResponse<WebServiceDto>>();

            return app;
        }
    }
}

public sealed class GetWebServicesHandler(
    LoadWebServicePage loadWebServicePage,
    LoadResourceDeploymentSets loadResourceDeploymentSets,
    IGitOpsMutations git)
    : IMediatorOutcomeHandler<GetWebServicesRequest, GetWebServicesOutcome>
{
    public async Task<GetWebServicesOutcome> HandleAsync(
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
            new LoadResourceDeploymentSetsRequest(
                TenantId: request.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceIds: page.Items.Select(service => service.Id).ToArray()),
            cancellationToken: cancellationToken);

        var statuses = await git.StatusesAsync(
            new LoadGitResourceStatusesRequest(
                TenantId: request.TenantId,
                Kind: "WebService",
                ResourceIds: page.Items.Select(item => item.Id).ToArray()),
            ct: cancellationToken);
        return new PaginatedResponse<WebServiceDto>(
            Items: page.Items.Select(service => service.ToDto(
                placements.GetValueOrDefault(service.Id)) with
            { GitOps = statuses.GetValueOrDefault(service.Id) }),
            TotalCount: page.TotalCount,
            PageNumber: request.PageNumber,
            PageSize: request.PageSize);
    }
}

public union GetWebServicesOutcome(PaginatedResponse<WebServiceDto>);

public record GetWebServicesRequest
    : PaginatedRequest, IMediatorRequest<GetWebServicesOutcome>, ITenantReadOperation
{
    [FromTenantHeader] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public string? ResourceGroupId { get; init; }
    [FromQuery] public string? Search { get; init; }
}
