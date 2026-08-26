using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Persistence.Members;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Endpoints.Members;

public sealed record DeleteTenantMemberRequest(
    [FromRoute] string TenantId,
    [FromRoute] string MemberId
) : IMediatorRequest<DeleteTenantMemberOutcome>, ITenantMemberTargetOperation;

public union DeleteTenantMemberError(TenantMemberMutationError);

public union DeleteTenantMemberOutcome(string, DeleteTenantMemberError);

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
        return persisted.Match(
            _ => new DeleteTenantMemberOutcome(request.MemberId),
            error => new DeleteTenantMemberOutcome(new DeleteTenantMemberError(error)));
    }
}

public static class DeleteTenantMemberEndpoint
{
    public static IEndpointRouteBuilder MapDeleteTenantMemberEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{TenantId}/members/{MemberId}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] DeleteTenantMemberRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    string => Results.NoContent(),
                    DeleteTenantMemberError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("DeleteTenantMember")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
