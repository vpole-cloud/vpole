using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Tenants.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Endpoints;

public static class GetTenants
{
    public static IEndpointRouteBuilder MapGetTenantsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetTenantsRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(Results.Ok);
        })
        .WithName("GetTenants")
        .Produces<IReadOnlyCollection<TenantDto>>();

        return app;
    }
}

public sealed record GetTenantsRequest(
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId
) : IMediatorRequest<IReadOnlyCollection<TenantDto>>;

public sealed class GetTenantsHandler(
    ITenantDbContext dbContext)
    : IMediatorHandler<GetTenantsRequest, IReadOnlyCollection<TenantDto>>
{
    public async Task<Result<IReadOnlyCollection<TenantDto>>> HandleAsync(
        GetTenantsRequest request,
        CancellationToken cancellationToken = default)
    {
        var memberTenants = await dbContext.Tenants
            .Find(t => t.Members.Any(m => m.UserId == request.CurrentUserId))
            .ToListAsync(cancellationToken);

        return memberTenants
            .Select(t =>
            {
                var member = t.Members.First(m => m.UserId == request.CurrentUserId);
                return t.ToDto(member.Role, member.IsDefault);
            })
            .ToList();
    }
}

