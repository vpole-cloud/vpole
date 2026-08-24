using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Persistence;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints;

public sealed record GetTenantMembersRequest(
    [FromRoute] string TenantId
) : IMediatorRequest<GetTenantMembersOutcome>, ITenantMemberReadOperation;

public union GetTenantMembersOutcome(
    IReadOnlyCollection<TenantMemberDto>,
    TenantNotFound);

public sealed class GetTenantMembersHandler(LoadTenant loadTenant)
    : IMediatorOutcomeHandler<GetTenantMembersRequest, GetTenantMembersOutcome>
{
    public async Task<GetTenantMembersOutcome> HandleAsync(
        GetTenantMembersRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenant = await loadTenant(request.TenantId, cancellationToken);

        return tenant is null
            ? new TenantNotFound(request.TenantId)
            : tenant.Members.Select(m => m.ToDto()).ToList();
    }
}

public static class GetTenantMembersEndpoint
{
    public static IEndpointRouteBuilder MapGetTenantMembersEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{TenantId}/members", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetTenantMembersRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyCollection<TenantMemberDto> members => Results.Ok(members),
                    TenantNotFound error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("GetTenantMembers")
            .Produces<IReadOnlyCollection<TenantMemberDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }
}
