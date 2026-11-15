using FluentValidation;
using MongoDB.Bson;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints.Invitations;

public sealed record RevokeTenantInvitationRequest(
    ManageInvitationRequest Invitation
) : IMediatorRequest<RevokeTenantInvitationOutcome>, ITenantMemberManageOperation
{
    public string TenantId => Invitation.TenantId;
}

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
        RuleFor(request => request.Invitation.TenantId).Must(value => ObjectId.TryParse(value, out _));
        RuleFor(request => request.Invitation.InvitationId).InvitationId();
        RuleFor(request => request.Invitation.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class RevokeTenantInvitationHandler(
    ManageInvitation management
) : IMediatorOutcomeHandler<RevokeTenantInvitationRequest, RevokeTenantInvitationOutcome>
{
    public Task<RevokeTenantInvitationOutcome> HandleAsync(
        RevokeTenantInvitationRequest request,
        CancellationToken cancellationToken = default) => management.RevokeAsync(request.Invitation, cancellationToken);
}

public static class RevokeTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapRevokeTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{tenantId}/invitations/{invitationId}/revoke",
                async (
                    string tenantId, string invitationId,
                    [FromBody] InvitationRevisionBody body, [FromClaim(Claims.UserId)] ClaimValue currentUserId,
                    IMediator mediator, CancellationToken ct) =>
                {
                    var result = await mediator.Send(
                        new RevokeTenantInvitationRequest(
                            new ManageInvitationRequest(
                                tenantId,
                                invitationId,
                                body.ExpectedRevision,
                                new InvitationActor(currentUserId))),
                        ct);
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
