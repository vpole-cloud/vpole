using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Application.Persistence;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.Domains.Application.Endpoints;

public static class GetDomainZone
{
    public static IEndpointRouteBuilder MapGetDomainZoneEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}", async (
                IMediator mediator,
                [AsParameters] GetDomainZoneRequest request,
                CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(ToHttpResult))
            .WithName("GetDomainZone")
            .Produces<TenantDomainZoneSnapshot>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static IResult ToHttpResult(GetDomainZoneOutcome outcome) => outcome switch
    {
        TenantDomainZoneSnapshot zone => Results.Ok(zone),
        DomainZoneNotFound notFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Domain zone not found",
            type: "https://pole.dev/problems/domain-zone-not-found",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "domains.zone.not-found",
                ["domainZoneId"] = notFound.ZoneId
            })
    };
}

public sealed record GetDomainZoneRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<GetDomainZoneOutcome>, ITenantReadOperation;

public union GetDomainZoneOutcome(
    TenantDomainZoneSnapshot,
    DomainZoneNotFound);

public sealed class GetDomainZoneRequestValidator : AbstractValidator<GetDomainZoneRequest>
{
    public GetDomainZoneRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Domain zone ID");
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
    }
}

public sealed class GetDomainZoneHandler(LoadTenantDomainZone loadTenantDomainZone)
    : IMediatorOutcomeHandler<GetDomainZoneRequest, GetDomainZoneOutcome>
{
    public async Task<GetDomainZoneOutcome> HandleAsync(
        GetDomainZoneRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await loadTenantDomainZone(
            request.TenantId,
            request.Id,
            cancellationToken);

        return result.Match(
            GetDomainZoneOutcome (zone) => zone,
            GetDomainZoneOutcome (error) => error);
    }
}
