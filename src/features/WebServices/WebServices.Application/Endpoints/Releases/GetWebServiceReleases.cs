using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Mediator;

namespace Pole.Features.WebServices.Application.Endpoints.Releases;

public static class GetWebServiceReleases
{
    public static IEndpointRouteBuilder MapGetWebServiceReleasesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/releases", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetWebServiceReleasesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    WebServiceReleaseHistoryDto value => Results.Ok(value),
                    WebServiceNotFound error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("GetWebServiceReleases")
            .Produces<WebServiceReleaseHistoryDto>();

        return app;
    }
}

public union GetWebServiceReleasesOutcome(
    WebServiceReleaseHistoryDto,
    WebServiceNotFound);

public sealed record GetWebServiceReleasesRequest : IMediatorRequest<GetWebServiceReleasesOutcome>, ITenantReadOperation
{
    [FromRoute] public string Id { get; init; } = string.Empty;
    [FromTenantHeader] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public int? PageNumber { get; init; }
    [FromQuery] public int? PageSize { get; init; }
    public int EffectivePageNumber => PageNumber.GetValueOrDefault(1);
    public int EffectivePageSize => PageSize.GetValueOrDefault(10);
    public int Skip => (EffectivePageNumber - 1) * EffectivePageSize;
}

public sealed class GetWebServiceReleasesHandler(
    LoadWebServiceReleaseHistory loadWebServiceReleaseHistory)
    : IMediatorOutcomeHandler<GetWebServiceReleasesRequest, GetWebServiceReleasesOutcome>
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

        return new GetWebServiceReleasesOutcome(new WebServiceReleaseHistoryDto(
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
        RuleFor(x => x.PageNumber).GreaterThan(0).When(x => x.PageNumber.HasValue);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).When(x => x.PageSize.HasValue);
    }
}
