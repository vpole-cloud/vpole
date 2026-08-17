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
using Pole.Validation;

namespace Pole.Features.Domains.Application.Endpoints;

public static class GetDomainZones
{
    public static IEndpointRouteBuilder MapGetDomainZonesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                IMediator mediator,
                [AsParameters] GetDomainZonesRequest request,
                CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(Results.Ok))
            .WithName("GetDomainZones")
            .Produces<IReadOnlyList<TenantDomainZoneSnapshot>>();
        return app;
    }
}

public sealed record GetDomainZonesRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromQuery] string? DeploymentTargetId)
    : IMediatorRequest<IReadOnlyList<TenantDomainZoneSnapshot>>, ITenantReadOperation;

public sealed class GetDomainZonesRequestValidator : AbstractValidator<GetDomainZonesRequest>
{
    public GetDomainZonesRequestValidator()
    {
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
        When(
            request => request.DeploymentTargetId is not null,
            () => RuleFor(request => request.DeploymentTargetId!)
                .MongoObjectId("Deployment target ID"));
    }
}

public sealed class GetDomainZonesHandler(LoadTenantDomainZones loadTenantDomainZones)
    : IMediatorOutcomeHandler<GetDomainZonesRequest, IReadOnlyList<TenantDomainZoneSnapshot>>
{
    public Task<IReadOnlyList<TenantDomainZoneSnapshot>> HandleAsync(
        GetDomainZonesRequest request,
        CancellationToken cancellationToken = default) =>
        loadTenantDomainZones(
            request.TenantId,
            request.DeploymentTargetId,
            cancellationToken);
}
