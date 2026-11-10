using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Domains.Application.Common.DomainHttpMappings;

namespace Pole.Features.Domains.Application.Endpoints;

public static class GetDomainZone
{
    public static IEndpointRouteBuilder MapGetDomainZoneEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}",
                async (
                        IMediator mediator, [AsParameters] GetDomainZoneRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(ToHttpResult))
            .WithName("GetDomainZone")
            .Produces<DomainZoneDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static IResult ToHttpResult(GetDomainZoneOutcome outcome) => outcome switch
    {
        DomainZoneDto zone => Results.Ok(zone),
        DomainZoneNotFound notFound => MapDomainZoneNotFound(notFound),
    };
}

public sealed record GetDomainZoneRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetDomainZoneOutcome>, ITenantReadOperation;

public union GetDomainZoneOutcome(DomainZoneDto, DomainZoneNotFound);

public sealed class GetDomainZoneRequestValidator : AbstractValidator<GetDomainZoneRequest>
{
    public GetDomainZoneRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Domain zone ID");
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
    }
}

public sealed class GetDomainZoneHandler(
    LoadTenantDomainZone loadTenantDomainZone,
    IGitOpsMutations git
) : IMediatorOutcomeHandler<GetDomainZoneRequest, GetDomainZoneOutcome>
{
    public async Task<GetDomainZoneOutcome> HandleAsync(
        GetDomainZoneRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await loadTenantDomainZone(request.TenantId, request.Id, cancellationToken);

        return result switch
        {
            TenantDomainZoneSnapshot zone => DomainZoneDto.FromSnapshot(
                zone,
                await git.StatusAsync(
                    new LoadGitResourceStatusRequest(request.TenantId, "DomainZone", zone.Id),
                    cancellationToken)),
            DomainZoneNotFound error => error,
        };
    }
}
