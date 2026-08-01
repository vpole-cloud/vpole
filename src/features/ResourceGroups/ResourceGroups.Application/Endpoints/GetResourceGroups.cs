using FluentValidation;
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
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class GetResourceGroups
{
    public static IEndpointRouteBuilder MapGetResourceGroupsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetResourceGroupsRequest request) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetResourceGroups")
            .WithSummary("Retrieves all resource groups.")
            .WithDescription("Retrieves a list of all resource groups available in the system.")
            .Produces<IEnumerable<ResourceGroupDto>>(StatusCodes.Status200OK);

        return app;
    }
}

public record GetResourceGroupsRequest : PaginatedRequest<ResourceGroupDto>, ITenantReadOperation
{
    [FromHeader(Name = TenantHeaders.TenantId)] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public string? Search { get; init; }
}


public sealed class GetResourceGroupsHandler(
    IResourceGroupDbContext dbContext)
    : IMediatorHandler<GetResourceGroupsRequest, PaginatedResponse<ResourceGroupDto>>
{
    public async Task<Result<PaginatedResponse<ResourceGroupDto>>> HandleAsync(
        GetResourceGroupsRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<ResourceGroup>.Filter.ForTenant(request.TenantId);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var pattern = $".*{Regex.Escape(request.Search)}.*";
            filter &= Builders<ResourceGroup>.Filter.Regex(rg => rg.Name, new BsonRegularExpression(pattern, "i"));
        }

        var totalCount = await dbContext.ResourceGroups.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var items = await dbContext.ResourceGroups.Find(filter)
            .ApplyPagination(request)
            .ToListAsync(cancellationToken);

        return new PaginatedResponse<ResourceGroupDto>(
            Items: items.Select(x => x.ToDto()),
            TotalCount: totalCount,
            PageNumber: request.PageNumber,
            PageSize: request.PageSize);
    }
}

public class GetResourceGroupsRequestValidator : AbstractValidator<GetResourceGroupsRequest>
{
    public GetResourceGroupsRequestValidator()
    {
    }
}
