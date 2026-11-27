// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Application.Policies;
using Pole.Features.Quotas.Application.Reservations;
using Pole.Features.Quotas.Application.Usage;
using Pole.Mediator;

namespace Pole.Features.Quotas.Application.Endpoints;

public static class GetTenantQuotaUsage
{
    public static IEndpointRouteBuilder MapGetTenantQuotaUsageEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/usage",
                async (
                    IMediator mediator, [AsParameters] GetTenantQuotaUsageRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        TenantQuotaUsageDto usage => Results.Ok(usage),
                    });
                })
            .WithName("GetTenantQuotaUsage")
            .Produces<TenantQuotaUsageDto>();

        return app;
    }
}

public sealed record GetTenantQuotaUsageRequest(
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetTenantQuotaUsageOutcome>, ITenantReadOperation;

public union GetTenantQuotaUsageOutcome(TenantQuotaUsageDto);

public sealed class GetTenantQuotaUsageHandler(
    LoadTenantQuotaUsage loadTenantQuotaUsage,
    LoadTenantQuotaPolicy loadTenantQuotaPolicy,
    LoadActiveQuotaReservations loadActiveQuotaReservations,
    TimeProvider timeProvider,
    QuotaCatalog catalog
) : IMediatorOutcomeHandler<GetTenantQuotaUsageRequest, GetTenantQuotaUsageOutcome>
{
    public async Task<GetTenantQuotaUsageOutcome> HandleAsync(
        GetTenantQuotaUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var usage = await loadTenantQuotaUsage(request.TenantId, cancellationToken);
        var policy = await loadTenantQuotaPolicy(request.TenantId, cancellationToken);
        var activeReservations = await loadActiveQuotaReservations(request.TenantId, nowUtc, cancellationToken);

        return usage.ToDto(activeReservations, policy, request.TenantId, catalog);
    }
}
