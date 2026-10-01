using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.PrivateAccess.Application.Persistence;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

public sealed record RevokePrivateAccessGrantRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default)
    : IMediatorRequest<RevokePrivateAccessGrantOutcome>, ITenantWriteOperation;

public union RevokePrivateAccessGrantOutcome(
    PrivateAccessGrantRevoked,
    PrivateAccessManageRoleRequired,
    PrivateAccessGrantMissing);

public sealed class RevokePrivateAccessGrantHandler(RevokePrivateAccessGrant revoke)
    : IMediatorOutcomeHandler<RevokePrivateAccessGrantRequest, RevokePrivateAccessGrantOutcome>
{
    public async Task<RevokePrivateAccessGrantOutcome> HandleAsync(
        RevokePrivateAccessGrantRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ActorUserId.Value))
            return new PrivateAccessManageRoleRequired();
        var outcome = await revoke(request.TenantId, request.Id,
            request.ActorUserId.Value, cancellationToken);
        return outcome switch
        {
            PrivateAccessGrantRevoked revoked => revoked,
            PrivateAccessManageRoleRequired error => error,
            PrivateAccessGrantMissing error => error
        };
    }
}

public sealed class RevokePrivateAccessGrantValidator : AbstractValidator<RevokePrivateAccessGrantRequest>
{
    public RevokePrivateAccessGrantValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Grant ID");
    }
}

public static class RevokePrivateAccessGrantEndpoint
{
    public static IEndpointRouteBuilder MapRevokePrivateAccessGrantEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                [AsParameters] RevokePrivateAccessGrantRequest request,
                CancellationToken cancellationToken) =>
            (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
            {
                PrivateAccessGrantRevoked => Results.NoContent(),
                PrivateAccessManageRoleRequired error => PrivateAccessHttpProblemMappings.Map(error),
                PrivateAccessGrantMissing error => PrivateAccessHttpProblemMappings.Map(error)
            }))
            .WithName("RevokePrivateAccessGrant")
            .Produces(StatusCodes.Status204NoContent);
        return app;
    }
}
