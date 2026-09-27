using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.Request;
using Pole.Mediator;

namespace Pole.Features.ResourceConnections.Application.Endpoints;

public sealed record GetResourceConnectionsRequest
    : IMediatorRequest<GetResourceConnectionsOutcome>, ITenantReadOperation
{
    [FromTenantHeader] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public int PageNumber { get; init; } = 1;
    [FromQuery] public int PageSize { get; init; } = 10;
}

public union GetResourceConnectionsOutcome(PaginatedResponse<ResourceConnectionDto>);

public sealed class GetResourceConnectionsRequestValidator
    : AbstractValidator<GetResourceConnectionsRequest>
{
    public GetResourceConnectionsRequestValidator()
    {
        RuleFor(request => request.PageNumber).GreaterThan(0);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
        RuleFor(request => request)
            .Must(request => request.PageNumber <= 0 || request.PageSize <= 0 ||
                (long)(request.PageNumber - 1) * request.PageSize <= int.MaxValue)
            .WithMessage("The requested page offset is too large.");
    }
}

public sealed class GetResourceConnectionsHandler(
    LoadResourceConnectionsPage loadPage)
    : IMediatorOutcomeHandler<GetResourceConnectionsRequest, GetResourceConnectionsOutcome>
{
    public async Task<GetResourceConnectionsOutcome> HandleAsync(
        GetResourceConnectionsRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await loadPage(
            request.TenantId,
            request.PageNumber,
            request.PageSize,
            cancellationToken);
        return new PaginatedResponse<ResourceConnectionDto>(
            page.Items.Select(connection => connection.ToDto()).ToArray(),
            page.TotalCount,
            request.PageNumber,
            request.PageSize);
    }
}

public static class GetResourceConnectionsEndpoint
{
    public static IEndpointRouteBuilder MapGetResourceConnectionsEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                IMediator mediator,
                [AsParameters] GetResourceConnectionsRequest request,
                CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken))
                .ToHttpResult(outcome => outcome switch
                {
                    PaginatedResponse<ResourceConnectionDto> page => Results.Ok(page)
                }))
            .WithName("GetResourceConnections")
            .Produces<PaginatedResponse<ResourceConnectionDto>>();
        return app;
    }
}
