using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints.Invitations;

public sealed record RevokeTenantInvitationRequest(
    [FromRoute] string TenantId,
    [FromRoute] string InvitationId,
    [FromBody] InvitationRevisionBody Body,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId
) : IMediatorRequest<RevokeTenantInvitationOutcome>, ITenantMemberManageOperation;

public union RevokeTenantInvitationOutcome(
    InvitationDto,
    InvitationNotFound,
    InvitationRevisionConflict,
    InvitationForbidden,
    InvitationUnavailable);

public sealed class RevokeTenantInvitationValidator : AbstractValidator<RevokeTenantInvitationRequest>
{
    public RevokeTenantInvitationValidator()
    {
        RuleFor(request => request.TenantId).RouteObjectId();
        RuleFor(request => request.InvitationId).InvitationId();
        RuleFor(request => request.Body.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class RevokeTenantInvitationHandler(
    ManageInvitation management
) : IMediatorOutcomeHandler<RevokeTenantInvitationRequest, RevokeTenantInvitationOutcome>
{
    public Task<RevokeTenantInvitationOutcome> HandleAsync(
        RevokeTenantInvitationRequest request,
        CancellationToken cancellationToken = default) => management.RevokeAsync(
        new ManageInvitationRequest(
            request.TenantId,
            request.InvitationId,
            request.Body.ExpectedRevision,
            new InvitationActor(request.CurrentUserId)),
        cancellationToken);
}

public static class RevokeTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapRevokeTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{TenantId}/invitations/{InvitationId}/revoke",
                async (
                    [AsParameters] RevokeTenantInvitationRequest request, IMediator mediator,
                    CancellationToken ct) =>
                {
                    var result = await mediator.Send(request, ct);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        InvitationDto invitation => Results.Ok(invitation),
                        InvitationNotFound error => InvitationHttpProblems.NotFound(error),
                        InvitationRevisionConflict error => InvitationHttpProblems.RevisionConflict(error),
                        InvitationForbidden error => InvitationHttpProblems.Forbidden(error),
                        InvitationUnavailable error => InvitationHttpProblems.Unavailable(error),
                    });
                })
            .WithName("RevokeTenantInvitation");
        return app;
    }
}
