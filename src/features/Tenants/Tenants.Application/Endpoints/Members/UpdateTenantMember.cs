using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Persistence.Members;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Endpoints.Members;

public sealed record UpdateTenantMemberRequest(
    [FromRoute] string TenantId,
    [FromRoute] string MemberId,
    [FromBody] UpdateTenantMemberRequestBody Body
) : IMediatorRequest<UpdateTenantMemberOutcome>, ITenantMemberRoleOperation
{
    public TenantRole Role => Body.Role;
}

public sealed record UpdateTenantMemberRequestBody(TenantRole Role);

public union UpdateTenantMemberError(TenantMemberMutationError);

public union UpdateTenantMemberOutcome(
    TenantMemberDto,
    UpdateTenantMemberError);

public sealed class UpdateTenantMemberHandler(
    PersistUpdatedTenantMemberRole persistUpdatedTenantMemberRole)
    : IMediatorOutcomeHandler<UpdateTenantMemberRequest, UpdateTenantMemberOutcome>
{
    public async Task<UpdateTenantMemberOutcome> HandleAsync(
        UpdateTenantMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        var persisted = await persistUpdatedTenantMemberRole(
            new UpdateTenantMemberRole(
                request.TenantId,
                request.MemberId,
                request.Body.Role),
            cancellationToken);
        return TypedResultCompositionExtensions.Match(
            persisted,
            updated => new UpdateTenantMemberOutcome(updated.ToDto()),
            error => new UpdateTenantMemberOutcome(new UpdateTenantMemberError(error)));
    }
}

public static class UpdateTenantMemberEndpoint
{
    public static IEndpointRouteBuilder MapUpdateTenantMemberEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{TenantId}/members/{MemberId}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] UpdateTenantMemberRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    TenantMemberDto member => Results.Ok(member),
                    UpdateTenantMemberError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("UpdateTenantMember")
            .Produces<TenantMemberDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
