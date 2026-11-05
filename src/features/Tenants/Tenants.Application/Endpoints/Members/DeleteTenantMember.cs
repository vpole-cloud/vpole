using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Members;
using Pole.Mediator;
using static Pole.Features.Tenants.Application.TenantMemberAuthorizationHttpProblemMappings;

namespace Pole.Features.Tenants.Application.Endpoints.Members;

public sealed record DeleteTenantMemberRequest(
    [FromRoute] string TenantId,
    [FromRoute] string MemberId
) : IMediatorRequest<DeleteTenantMemberOutcome>, ITenantMemberTargetOperation;

public union DeleteTenantMemberOutcome(
    TenantMemberRemoved,
    TenantNotFound,
    TenantMemberNotFound,
    TenantLastOwner);

public sealed class DeleteTenantMemberHandler(
    PersistRemovedTenantMember persistRemovedTenantMember)
    : IMediatorOutcomeHandler<DeleteTenantMemberRequest, DeleteTenantMemberOutcome>
{
    public async Task<DeleteTenantMemberOutcome> HandleAsync(
        DeleteTenantMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        var persisted = await persistRemovedTenantMember(
            new RemoveTenantMember(request.TenantId, request.MemberId),
            cancellationToken);
        return persisted switch
        {
            TenantMemberRemoved removed => removed,
            TenantNotFound error => error,
            TenantMemberNotFound error => error,
            TenantLastOwner error => error
        };
    }
}

public static class DeleteTenantMemberEndpoint
{
    public static IEndpointRouteBuilder MapDeleteTenantMemberEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{TenantId}/members/{MemberId}", async (
                IMediator mediator,
                [AsParameters] DeleteTenantMemberRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    TenantMemberRemoved => Results.NoContent(),
                    TenantNotFound error => MapTenantNotFound(error),
                    TenantMemberNotFound error => MapMemberNotFound(error),
                    TenantLastOwner error => MapLastOwner(error)
                });
            })
            .WithName("DeleteTenantMember")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
