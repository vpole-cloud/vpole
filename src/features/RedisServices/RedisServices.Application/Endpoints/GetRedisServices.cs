using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.Request;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class GetRedisServices
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGetRedisServicesEndpoint()
        {
            app.MapGet("/", async (IMediator mediator, [AsParameters] GetRedisServicesRequest request) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetRedisServices")
            .WithSummary("Retrieves all Redis services.")
            .WithDescription("Fetches a list of all Redis services available in the system.")
            .Produces<List<RedisDto>>();

            return app;
        }
    }
}

public sealed class GetRedisServicesHandler(
    LoadRedisPage loadRedisPage,
    LoadResourceDeployments loadDeployments)
    : IMediatorHandler<GetRedisServicesRequest, PaginatedResponse<RedisDto>>
{
    public async Task<Result<PaginatedResponse<RedisDto>>> HandleAsync(
        GetRedisServicesRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await loadRedisPage(
            new RedisListQuery(
                request.TenantId,
                request.Search,
                request.ResourceGroupId,
                request.PageNumber,
                request.PageSize),
            cancellationToken);
        var placements = await loadDeployments(
            request.TenantId,
            RedisResourceDeployment.ResourceType,
            page.Items.Select(redis => redis.Id).ToArray(),
            cancellationToken);

        return new PaginatedResponse<RedisDto>(
            Items: page.Items.Select(redis => redis.ToDto(
                placements.GetValueOrDefault(redis.Id))),
            TotalCount: page.TotalCount,
            PageNumber: request.PageNumber,
            PageSize: request.PageSize);
    }
}

public record GetRedisServicesRequest : PaginatedRequest<RedisDto>, ITenantReadOperation
{
    [FromHeader(Name = TenantHeaders.TenantId)] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public string? Search { get; init; }
    [FromQuery] public string? ResourceGroupId { get; init; }
}
