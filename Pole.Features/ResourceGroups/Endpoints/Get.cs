using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Pole.Database;
using Pole.Features.Request;
using Pole.Features.ResourceGroups.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Endpoints;

public static class Get
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

public record GetResourceGroupsRequest : PaginatedRequest<ResourceGroupDto>
{
    [FromQuery] public string? Search { get; init; }
}


public sealed class GetResourceGroupsHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<GetResourceGroupsRequest, PaginatedResponse<ResourceGroupDto>>
{
    public async Task<Result<PaginatedResponse<ResourceGroupDto>>> HandleAsync(
        GetResourceGroupsRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.ResourceGroups.AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(rg => rg.Name.Contains(request.Search, StringComparison.OrdinalIgnoreCase));

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
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