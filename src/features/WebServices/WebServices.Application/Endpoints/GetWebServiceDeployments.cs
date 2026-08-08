using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class GetWebServiceDeployments
{
    public static IEndpointRouteBuilder MapGetWebServiceDeploymentsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/deployments", async (IMediator mediator, [AsParameters] GetWebServiceDeploymentsRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetWebServiceDeployments")
            .Produces<WebServiceReleaseHistoryDto>();

        return app;
    }
}

public sealed record GetWebServiceDeploymentsRequest : IMediatorRequest<WebServiceReleaseHistoryDto>, ITenantReadOperation
{
    [FromRoute] public string Id { get; init; } = string.Empty;
    [FromHeader(Name = TenantHeaders.TenantId)] public string TenantId { get; init; } = string.Empty;
    [FromQuery] public int? PageNumber { get; init; }
    [FromQuery] public int? PageSize { get; init; }
    public int EffectivePageNumber => PageNumber.GetValueOrDefault(1);
    public int EffectivePageSize => PageSize.GetValueOrDefault(10);
    public int Skip => (EffectivePageNumber - 1) * EffectivePageSize;
}

public sealed class GetWebServiceDeploymentsHandler(
    LoadWebServiceReleaseHistory loadWebServiceReleaseHistory)
    : IMediatorHandler<GetWebServiceDeploymentsRequest, WebServiceReleaseHistoryDto>
{
    public async Task<Result<WebServiceReleaseHistoryDto>> HandleAsync(
        GetWebServiceDeploymentsRequest request,
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
            return Failures.NotFound("Web service not found.", request.Id);

        return new WebServiceReleaseHistoryDto(
            history.WebService.ActiveRevision,
            new PaginatedResponse<WebServiceReleaseDto>(
                history.Releases.Select(release => release.ToDto()),
                history.TotalCount,
                request.EffectivePageNumber,
                request.EffectivePageSize));
    }
}

public sealed class GetWebServiceDeploymentsValidator : AbstractValidator<GetWebServiceDeploymentsRequest>
{
    public GetWebServiceDeploymentsValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.PageNumber).GreaterThan(0).When(x => x.PageNumber.HasValue);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).When(x => x.PageSize.HasValue);
    }
}
