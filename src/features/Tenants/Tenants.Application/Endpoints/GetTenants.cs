using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Persistence;
using Pole.Features.Tenants.Application.Persistence.Members;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints;

public static class GetTenants
{
    public static IEndpointRouteBuilder MapGetTenantsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetTenantsRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(outcome => outcome switch
            {
                IReadOnlyCollection<TenantDto> tenants => Results.Ok(tenants)
            });
        })
        .WithName("GetTenants")
        .Produces<IReadOnlyCollection<TenantDto>>();

        return app;
    }
}

public sealed record GetTenantsRequest(
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId
) : IMediatorRequest<GetTenantsOutcome>;

public union GetTenantsOutcome(IReadOnlyCollection<TenantDto>);

public sealed class GetTenantsHandler(
    LoadTenantsForUser loadTenantsForUser)
    : IMediatorOutcomeHandler<GetTenantsRequest, GetTenantsOutcome>
{
    public async Task<GetTenantsOutcome> HandleAsync(
        GetTenantsRequest request,
        CancellationToken cancellationToken = default)
    {
        var memberTenants = await loadTenantsForUser(
            request.CurrentUserId,
            cancellationToken);

        return memberTenants
            .Select(t =>
            {
                var member = t.Members.First(m => m.UserId == request.CurrentUserId);
                return t.ToDto(member.Role, member.IsDefault);
            })
            .ToList();
    }
}
