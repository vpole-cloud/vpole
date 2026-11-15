using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.SeaweedFS.Application.Endpoints;

using Pole.Features.ResourceDeployments.Contracts;

public sealed record ListSeaweedFSOutcome(
    IReadOnlyList<SeaweedFSServiceDto> Items,
    long TotalCount,
    int PageNumber,
    int PageSize
);

public sealed record ListSeaweedFSRequest(
    [FromTenantHeader] string TenantId,
    [FromQuery] string? Search = null,
    [FromQuery] string? ResourceGroupId = null,
    [FromQuery] int PageNumber = 1,
    [FromQuery] int PageSize = 20
) : IMediatorRequest<ListSeaweedFSOutcome>, ITenantReadOperation;

public sealed class ListSeaweedFSValidator : AbstractValidator<ListSeaweedFSRequest>
{
    public ListSeaweedFSValidator()
    {
        RuleFor(x => x.Search).MaximumLength(100);
        RuleFor(x => x.PageNumber).InclusiveBetween(1, 1000000);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.ResourceGroupId).OptionalMongoObjectId("Resource group ID");
    }
}

public sealed class ListSeaweedFSHandler(
    SeaweedFSQueries query,
    LoadResourceDeployments deployments
) : IMediatorOutcomeHandler<ListSeaweedFSRequest, ListSeaweedFSOutcome>
{
    public async Task<ListSeaweedFSOutcome> HandleAsync(
        ListSeaweedFSRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await query.PageAsync(
            request.TenantId,
            request.Search,
            request.ResourceGroupId,
            request.PageNumber,
            request.PageSize,
            cancellationToken);
        var placement = await deployments(
            new(request.TenantId, SeaweedFSResourceDeployment.ResourceType, page.Items.Select(x => x.Id).ToArray()),
            cancellationToken);
        return new ListSeaweedFSOutcome(
            page.Items.Select(x => x.ToDto(placement.GetValueOrDefault(x.Id))).ToArray(),
            page.Count,
            request.PageNumber,
            request.PageSize);
    }
}

public static class ListSeaweedFSEndpoint
{
    public static IEndpointRouteBuilder MapListSeaweedFSEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/",
                async (
                    IMediator mediator, [AsParameters] ListSeaweedFSRequest request,
                    CancellationToken ct) =>
                (await mediator.Send(request, ct)).ToHttpResult(value => Results.Ok(value)))
            .WithName("ListSeaweedFS");
        return app;
    }
}
