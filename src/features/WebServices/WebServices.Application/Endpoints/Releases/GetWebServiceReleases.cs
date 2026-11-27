// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Releases;
using Pole.Mediator;
using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints.Releases;

public static class GetWebServiceReleases
{
    public static IEndpointRouteBuilder MapGetWebServiceReleasesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}/releases",
                async (
                    IMediator mediator, [AsParameters] GetWebServiceReleasesRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        WebServiceReleaseHistoryDto value => Results.Ok(value),
                        WebServiceNotFound error => MapError(error),
                    });
                })
            .WithName("GetWebServiceReleases")
            .Produces<WebServiceReleaseHistoryDto>();

        return app;
    }
}

public union GetWebServiceReleasesOutcome(WebServiceReleaseHistoryDto, WebServiceNotFound);

public sealed record GetWebServiceReleasesRequest : IMediatorRequest<GetWebServiceReleasesOutcome>, ITenantReadOperation
{
    [FromRoute] public string Id { get; init; } = "";
    [FromQuery] public int? PageNumber { get; init; }
    [FromQuery] public int? PageSize { get; init; }
    public int EffectivePageNumber => PageNumber.GetValueOrDefault(1);
    public int EffectivePageSize => PageSize.GetValueOrDefault(10);
    public int Skip => (EffectivePageNumber - 1) * EffectivePageSize;
    [FromTenantHeader] public string TenantId { get; init; } = "";
}

public sealed class GetWebServiceReleasesHandler(
    LoadWebServiceReleaseHistory loadWebServiceReleaseHistory
) : IMediatorOutcomeHandler<GetWebServiceReleasesRequest, GetWebServiceReleasesOutcome>
{
    public async Task<GetWebServiceReleasesOutcome> HandleAsync(
        GetWebServiceReleasesRequest request,
        CancellationToken cancellationToken = default)
    {
        var history = await loadWebServiceReleaseHistory(
            new WebServiceReleaseHistoryQuery(
                request.TenantId,
                request.Id,
                request.EffectivePageNumber,
                request.EffectivePageSize),
            cancellationToken);
        if (history is null)
            return new WebServiceNotFound(request.Id);

        return new GetWebServiceReleasesOutcome(
            new WebServiceReleaseHistoryDto(
                history.WebService.ActiveRevision,
                new PaginatedResponse<WebServiceReleaseDto>(
                    history.Releases.Select(release => release.ToDto()),
                    history.TotalCount,
                    request.EffectivePageNumber,
                    request.EffectivePageSize)));
    }
}

public sealed class GetWebServiceReleasesValidator : AbstractValidator<GetWebServiceReleasesRequest>
{
    public GetWebServiceReleasesValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.PageNumber).InclusiveBetween(1, 10000).When(x => x.PageNumber.HasValue);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).When(x => x.PageSize.HasValue);
    }
}
