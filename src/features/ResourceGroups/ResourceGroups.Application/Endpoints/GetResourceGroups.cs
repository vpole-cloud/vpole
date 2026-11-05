using Pole.Features.GitOps.Contracts;
using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class GetResourceGroups
{
    public static IEndpointRouteBuilder MapGetResourceGroupsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                IMediator mediator,
                [AsParameters] GetResourceGroupsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    PaginatedResponse<ResourceGroupDto> page => Results.Ok(page)
                });
            })
            .WithName("GetResourceGroups")
            .WithSummary("Retrieves all resource groups.")
            .WithDescription("Retrieves a paginated list of resource groups in the selected tenant.")
            .Produces<PaginatedResponse<ResourceGroupDto>>(StatusCodes.Status200OK);

        return app;
    }
}

public record GetResourceGroupsRequest
    : IMediatorRequest<GetResourceGroupsOutcome>, ITenantReadOperation
{
    [FromQuery, DefaultValue(1)] public int PageNumber { get; init; } = 1;
    [FromQuery, DefaultValue(10)] public int PageSize { get; init; } = 10;
    [FromTenantHeader] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public string? Search { get; init; }
}

public sealed class GetResourceGroupsHandler(
    LoadResourceGroupPage loadResourceGroupPage,
    IGitOpsMutations git)
    : IMediatorOutcomeHandler<GetResourceGroupsRequest, GetResourceGroupsOutcome>
{
    public async Task<GetResourceGroupsOutcome> HandleAsync(
        GetResourceGroupsRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await loadResourceGroupPage(
            new ResourceGroupPageQuery(
                request.TenantId,
                request.PageNumber,
                request.PageSize,
                request.Search),
            cancellationToken);

        var statuses = await git.StatusesAsync(
            new LoadGitResourceStatusesRequest(
                TenantId: request.TenantId,
                Kind: "ResourceGroup",
                ResourceIds: page.Items.Select(item => item.ResourceGroup.Id).ToArray()),
            ct: cancellationToken);
        return new PaginatedResponse<ResourceGroupDto>(
            Items
                : page.Items.Select(x => x.ResourceGroup.ToDto(x.ResourceCount) with
                {
                    GitOps =
                statuses.GetValueOrDefault(x.ResourceGroup.Id)
                }),
            TotalCount: page.TotalCount,
            PageNumber: request.PageNumber,
            PageSize: request.PageSize);
    }
}

public union GetResourceGroupsOutcome(PaginatedResponse<ResourceGroupDto>);

public class GetResourceGroupsRequestValidator : AbstractValidator<GetResourceGroupsRequest>
{
    public GetResourceGroupsRequestValidator()
    {
        RuleFor(request => request.PageNumber).GreaterThan(0);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 200);
    }
}
