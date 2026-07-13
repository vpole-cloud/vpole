using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Tenants.Application.Common;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints;

public static class TenantMembers
{
    public static IEndpointRouteBuilder MapTenantMemberEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{TenantId}/members", async (IMediator mediator, [AsParameters] GetTenantMembersRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(Results.Ok);
        })
        .WithName("GetTenantMembers")
        .Produces<IReadOnlyCollection<TenantMemberDto>>();

        app.MapPost("/{TenantId}/members", async (IMediator mediator, [AsParameters] AddTenantMemberRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(m => Results.Created($"/api/v1/tenants/{m.TenantId}/members/{m.Id}", m));
        })
        .WithName("AddTenantMember")
        .Produces<TenantMemberDto>(StatusCodes.Status201Created);

        app.MapPut("/{TenantId}/members/{MemberId}", async (IMediator mediator, [AsParameters] UpdateTenantMemberRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(Results.Ok);
        })
        .WithName("UpdateTenantMember")
        .Produces<TenantMemberDto>();

        app.MapDelete("/{TenantId}/members/{MemberId}", async (IMediator mediator, [AsParameters] DeleteTenantMemberRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(_ => Results.NoContent());
        })
        .WithName("DeleteTenantMember")
        .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}
