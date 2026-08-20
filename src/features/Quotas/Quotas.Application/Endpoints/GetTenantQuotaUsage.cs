using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Domain;
using Pole.Mediator;

namespace Pole.Features.Quotas.Application.Endpoints;

public static class GetTenantQuotaUsage
{
    public static IEndpointRouteBuilder MapGetTenantQuotaUsageEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/usage", async (
                IMediator mediator,
                [AsParameters] GetTenantQuotaUsageRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetTenantQuotaUsage")
            .Produces<TenantQuotaUsageDto>();

        return app;
    }
}

public sealed record GetTenantQuotaUsageRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<TenantQuotaUsageDto>, ITenantReadOperation;

public sealed class GetTenantQuotaUsageHandler(
    IQuotaDbContext dbContext,
    TimeProvider timeProvider,
    QuotaCatalog catalog)
    : IMediatorOutcomeHandler<
        GetTenantQuotaUsageRequest,
        TenantQuotaUsageDto>
{
    public async Task<TenantQuotaUsageDto> HandleAsync(
        GetTenantQuotaUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var usage = await dbContext.TenantQuotaUsages
            .Find(candidate => candidate.TenantId == request.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        var policy = await dbContext.TenantQuotaPolicies
            .Find(candidate => candidate.TenantId == request.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        var activeReservations = await dbContext.QuotaReservations
            .Find(Builders<QuotaReservation>.Filter.And(
                Builders<QuotaReservation>.Filter.Eq(
                    reservation => reservation.TenantId,
                    request.TenantId),
                QuotaReservationFilters.Active(nowUtc)))
            .ToListAsync(cancellationToken);

        return usage.ToDto(activeReservations, policy, request.TenantId, catalog);
    }
}
