using System.Text.RegularExpressions;
using FluentValidation;
using MongoDB.Driver;
using Pole.Server.Database;
using Pole.Server.Features.Common.Abstractions;
using Pole.Server.Features.ResourceGroups.Common;
using Pole.Server.Mediator;
using Pole.Server.ResultType;

namespace Pole.Server.Features.ResourceGroups.Endpoints;

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

public record GetResourceGroupsRequest(
    string? Search
) : PaginatedRequest<ResourceGroupDto>;


public sealed class GetResourceGroupsHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<GetResourceGroupsRequest, PaginatedResponse<ResourceGroupDto>>
{
    public async Task<Result<PaginatedResponse<ResourceGroupDto>>> HandleAsync(
        GetResourceGroupsRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<ResourceGroup>.Filter.Empty;
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var pattern = $".*{Regex.Escape(request.Search)}.*";
            filter = Builders<ResourceGroup>.Filter.Regex(rg => rg.Name, new MongoDB.Bson.BsonRegularExpression(pattern, "i"));
        }

        var totalCount = await dbContext.ResourceGroups
            .CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var items = await dbContext.ResourceGroups
            .Find(filter)
            .ApplyPagination(request)
            .ToListAsync(cancellationToken: cancellationToken);

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
        RuleFor(x => x.PageNumber)
            .GreaterThan(0).WithMessage("Page number must be greater than 0.");

        RuleFor(x => x.PageSize)
            .GreaterThan(0).WithMessage("Page size must be greater than 0.")
            .LessThanOrEqualTo(100).WithMessage("Page size must not exceed 100.");
    }
}