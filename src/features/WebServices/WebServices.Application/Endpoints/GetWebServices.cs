using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
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
    IWebServiceDbContext dbContext,
    IResourceDeploymentQueryService deploymentQuery)
    : IMediatorHandler<GetWebServicesRequest, PaginatedResponse<WebServiceDto>>
{
    public async Task<Result<PaginatedResponse<WebServiceDto>>> HandleAsync(
        GetWebServicesRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<WebService>.Filter.ForTenant(request.TenantId);
        
        if (request.ResourceGroupId is not null)
        {
            filter &= Builders<WebService>.Filter.Eq(ws => ws.ResourceGroupId, request.ResourceGroupId);
        }
        if (!string.IsNullOrEmpty(request.Search))
        {
            var namePattern = new BsonRegularExpression($".*{Regex.Escape(request.Search)}.*", "i");
            var nameFilter = Builders<WebService>.Filter.Regex(ws => ws.Name, namePattern);
            filter &= ObjectId.TryParse(request.Search, out _)
                ? Builders<WebService>.Filter.Or(nameFilter, Builders<WebService>.Filter.Eq(ws => ws.Id, request.Search))
                : nameFilter;
        }
        
        var totalCount = await dbContext.WebServices.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var webServices = await dbContext.WebServices.Find(filter)
            .ApplyPagination(request)
            .ToListAsync(cancellationToken);
        var placements = await deploymentQuery.GetManyAsync(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webServices.Select(service => service.Id).ToArray(),
            cancellationToken);

        return new PaginatedResponse<WebServiceDto>(
            Items: webServices.Select(service => service.ToDto(
                placements.GetValueOrDefault(service.Id))),
            TotalCount: totalCount,
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
