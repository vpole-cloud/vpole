using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using FluentValidation;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.RedisServices.Application.Persistence.Reads;
using Pole.Features.Request;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class GetRedisServices
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGetRedisServicesEndpoint()
        {
            app.MapGet("/", async (
                    IMediator mediator,
                    [AsParameters] GetRedisServicesRequest request,
                    CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken))
                    .ToHttpResult(outcome => outcome switch
                    {
                        PaginatedResponse<RedisDto> page => Results.Ok(page)
                    }))
            .WithName("GetRedisServices")
            .WithSummary("Retrieves all Redis services.")
            .WithDescription("Fetches a list of all Redis services available in the system.")
            .Produces<List<RedisDto>>();

            return app;
        }
    }
}

public union GetRedisServicesOutcome(PaginatedResponse<RedisDto>);

public sealed class GetRedisServicesHandler(
    LoadRedisPage loadRedisPage,
    LoadResourceDeployments loadDeployments)
    : IMediatorOutcomeHandler<GetRedisServicesRequest, GetRedisServicesOutcome>
{
    public async Task<GetRedisServicesOutcome> HandleAsync(
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

        return new GetRedisServicesOutcome(new PaginatedResponse<RedisDto>(
            Items: page.Items.Select(redis => redis.ToDto(
                placements.GetValueOrDefault(redis.Id))),
            TotalCount: page.TotalCount,
            PageNumber: request.PageNumber,
            PageSize: request.PageSize));
    }
}

public sealed record GetRedisServicesRequest : IMediatorRequest<GetRedisServicesOutcome>, ITenantReadOperation
{
    [FromTenantHeader] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public string? Search { get; init; }
    [FromQuery] public string? ResourceGroupId { get; init; }
    [FromQuery] public int PageNumber { get; init; } = 1;
    [FromQuery] public int PageSize { get; init; } = 10;
}

public sealed class GetRedisServicesRequestValidator
    : AbstractValidator<GetRedisServicesRequest>
{
    public GetRedisServicesRequestValidator()
    {
        RuleFor(request => request.PageNumber).GreaterThan(0);
        RuleFor(request => request.PageSize).GreaterThan(0);
        RuleFor(request => request.ResourceGroupId)
            .OptionalMongoObjectId("Resource group ID");
    }
}
