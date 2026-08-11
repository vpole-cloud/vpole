using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Contracts;
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
                (await mediator.Send(request, cancellationToken)).ToHttpResult(Results.Ok))
            .WithName("GetDomainZone")
            .Produces<TenantDomainZoneSnapshot>();
        return app;
    }
}

public sealed record GetDomainZoneRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<TenantDomainZoneSnapshot>, ITenantReadOperation;

public sealed class GetDomainZoneRequestValidator : AbstractValidator<GetDomainZoneRequest>
{
    public GetDomainZoneRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Domain zone ID");
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
    }
}

public sealed class GetDomainZoneHandler(ITenantDomainZoneCapability zones)
    : IMediatorHandler<GetDomainZoneRequest, TenantDomainZoneSnapshot>
{
    public Task<Result<TenantDomainZoneSnapshot>> HandleAsync(
        GetDomainZoneRequest request,
        CancellationToken cancellationToken = default) =>
        zones.GetAsync(request.TenantId, request.Id, cancellationToken);
}
