using Pole.Features.GitOps.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Reads;
using Pole.Features.Request;
using Pole.Mediator;

namespace Pole.Features.Functions.Application.Endpoints;

public static class GetFunctionApps
{
    public static IEndpointRouteBuilder MapGetFunctionAppsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                IMediator mediator,
                [AsParameters] GetFunctionAppsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    PaginatedResponse<FunctionAppDto> page => Results.Ok(page)
                });
            })
            .WithName("GetFunctionApps")
            .Produces<PaginatedResponse<FunctionAppDto>>();
        return app;
    }
}

public sealed record GetFunctionAppsRequest
    : PaginatedRequest, IMediatorRequest<GetFunctionAppsOutcome>, ITenantReadOperation
{
    [FromTenantHeader] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public string? Search { get; init; }
    [FromQuery] public string? ResourceGroupId { get; init; }
}

public sealed class GetFunctionAppsHandler(
    LoadFunctionAppPage loadFunctionAppPage,
    IGitOpsMutations git)
    : IMediatorOutcomeHandler<GetFunctionAppsRequest, GetFunctionAppsOutcome>
{
    public async Task<GetFunctionAppsOutcome> HandleAsync(
        GetFunctionAppsRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await loadFunctionAppPage(
            new FunctionAppListQuery(
                request.TenantId,
                request.Search,
                request.ResourceGroupId,
                request.PageNumber,
                request.PageSize),
            cancellationToken);
        var statuses = await git.StatusesAsync(new(request.TenantId, "FunctionApp",
            page.Items.Select(item => item.Id).ToArray()), cancellationToken);
        return new PaginatedResponse<FunctionAppDto>(
            page.Items.Select(app => app.ToDto() with { GitOps = statuses.GetValueOrDefault(app.Id) }),
            page.TotalCount,
            request.PageNumber,
            request.PageSize);
    }
}

public union GetFunctionAppsOutcome(PaginatedResponse<FunctionAppDto>);
