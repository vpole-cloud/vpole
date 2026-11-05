using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Mongo;
using Pole.Features.Quotas.Application.Policies;
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
                return result.ToHttpResult(outcome => outcome switch
                {
                    TenantQuotaPolicyDto policy => Results.Ok(policy)
                });
            })
            .WithName("GetTenantQuotaPolicy")
            .RequireAuthorization(Policy.Admin)
            .Produces<TenantQuotaPolicyDto>();

        return app;
    }
}

public sealed record GetTenantQuotaPolicyRequest(
    [FromRoute] string TenantId)
    : IMediatorRequest<GetTenantQuotaPolicyOutcome>, IPlatformTenantOperation;

public union GetTenantQuotaPolicyOutcome(TenantQuotaPolicyDto);

public sealed class GetTenantQuotaPolicyHandler(
    LoadTenantQuotaPolicy loadTenantQuotaPolicy,
    QuotaCatalog catalog)
    : IMediatorOutcomeHandler<
        GetTenantQuotaPolicyRequest,
        GetTenantQuotaPolicyOutcome>
{
    public async Task<GetTenantQuotaPolicyOutcome> HandleAsync(
        GetTenantQuotaPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var policy = await loadTenantQuotaPolicy(request.TenantId, cancellationToken);

        return policy.ToDto(request.TenantId, catalog);
    }
}
