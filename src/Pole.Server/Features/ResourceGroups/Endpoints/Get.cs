using Pole.Server.Features.Common.Abstractions;
using Pole.Server.Features.ResourceGroups.Common;
using Pole.Server.Mediator;
using Pole.Server.ResultType;

namespace Pole.Server.Features.ResourceGroups.Endpoints;

public static class Get
{
    public static IEndpointRouteBuilder MapGetResourceGroupsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator) =>
            {
                var result = await mediator.Send(new GetResourceGroupsRequest());
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
}