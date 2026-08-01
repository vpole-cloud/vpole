using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
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

public sealed class GetWebServiceDeploymentsHandler(IWebServiceDbContext dbContext)
    : IMediatorHandler<GetWebServiceDeploymentsRequest, WebServiceReleaseHistoryDto>
{
    public async Task<Result<WebServiceReleaseHistoryDto>> HandleAsync(
        GetWebServiceDeploymentsRequest request,
        CancellationToken cancellationToken = default)
    {
        var webServiceFilter = Builders<WebService>.Filter.And(
            Builders<WebService>.Filter.ForTenant(request.TenantId),
            Builders<WebService>.Filter.Eq(service => service.Id, request.Id));

        var webService = await dbContext.WebServices
            .Find(webServiceFilter)
            .FirstOrDefaultAsync(cancellationToken);

        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);

        var releaseFilter = Builders<WebServiceRelease>.Filter.And(
            Builders<WebServiceRelease>.Filter.ForTenant(request.TenantId),
            Builders<WebServiceRelease>.Filter.Eq(release => release.WebServiceId, request.Id));

        var totalCount = await dbContext.WebServiceReleases
            .CountDocumentsAsync(releaseFilter, cancellationToken: cancellationToken);

        var releases = await dbContext.WebServiceReleases
            .Find(releaseFilter)
            .SortByDescending(release => release.Revision)
            .Skip(request.Skip)
            .Limit(request.EffectivePageSize)
            .ToListAsync(cancellationToken);

        return new WebServiceReleaseHistoryDto(
            webService.ActiveRevision,
            new PaginatedResponse<WebServiceReleaseDto>(
                releases.Select(release => release.ToDto()),
                totalCount,
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
