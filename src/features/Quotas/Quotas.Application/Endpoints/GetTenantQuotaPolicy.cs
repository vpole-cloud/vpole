using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Quotas.Application.Common;
using Pole.Mediator;

namespace Pole.Features.Quotas.Application.Endpoints;

public static class GetTenantQuotaPolicy
{
    public static IEndpointRouteBuilder MapGetTenantQuotaPolicyEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{tenantId}/quota", async (
                IMediator mediator,
                [AsParameters] GetTenantQuotaPolicyRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetTenantQuotaPolicy")
            .RequireAuthorization(Policy.Admin)
            .Produces<TenantQuotaPolicyDto>();

        return app;
    }
}

public sealed record GetTenantQuotaPolicyRequest(
    [FromRoute] string TenantId)
    : IMediatorRequest<TenantQuotaPolicyDto>;

public sealed class GetTenantQuotaPolicyHandler(
    IQuotaDbContext dbContext,
    QuotaCatalog catalog)
    : IMediatorOutcomeHandler<
        GetTenantQuotaPolicyRequest,
        TenantQuotaPolicyDto>
{
    public async Task<TenantQuotaPolicyDto> HandleAsync(
        GetTenantQuotaPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var policy = await dbContext.TenantQuotaPolicies
            .Find(candidate => candidate.TenantId == request.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return policy.ToDto(request.TenantId, catalog);
    }
}
