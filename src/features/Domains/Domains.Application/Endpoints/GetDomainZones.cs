// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.GitOps.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Domains.Application.Endpoints;

public static class GetDomainZones
{
    public static IEndpointRouteBuilder MapGetDomainZonesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/",
                async (
                        IMediator mediator, [AsParameters] GetDomainZonesRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        IReadOnlyList<DomainZoneDto> zones => Results.Ok(zones),
                    }))
            .WithName("GetDomainZones")
            .Produces<IReadOnlyList<DomainZoneDto>>();
        return app;
    }
}

public sealed record GetDomainZonesRequest(
    [FromTenantHeader] string TenantId,
    [FromQuery] string? DeploymentTargetId
) : IMediatorRequest<GetDomainZonesOutcome>, ITenantReadOperation;

public union GetDomainZonesOutcome(IReadOnlyList<DomainZoneDto>);

public sealed class GetDomainZonesRequestValidator : AbstractValidator<GetDomainZonesRequest>
{
    public GetDomainZonesRequestValidator()
    {
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
        When(
            request => request.DeploymentTargetId is not null,
            () => RuleFor(request => request.DeploymentTargetId!).MongoObjectId("Deployment target ID"));
    }
}

public sealed class GetDomainZonesHandler(
    LoadTenantDomainZones loadTenantDomainZones,
    IGitOpsMutations git
) : IMediatorOutcomeHandler<GetDomainZonesRequest, GetDomainZonesOutcome>
{
    public async Task<GetDomainZonesOutcome> HandleAsync(
        GetDomainZonesRequest request,
        CancellationToken cancellationToken = default)
    {
        var zones = await loadTenantDomainZones(request.TenantId, request.DeploymentTargetId, cancellationToken);
        if (zones.Count == 0)
            return Array.Empty<DomainZoneDto>();

        var ownership = await git.StatusesAsync(
            new LoadGitResourceStatusesRequest(request.TenantId, "DomainZone", zones.Select(item => item.Id).ToArray()),
            cancellationToken);
        return zones.Select(zone => DomainZoneDto.FromSnapshot(zone, ownership.GetValueOrDefault(zone.Id))).ToArray();
    }
}
