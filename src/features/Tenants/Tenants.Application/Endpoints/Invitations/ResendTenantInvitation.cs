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

public sealed record ResendTenantInvitationRequest(
    ManageInvitationRequest Invitation
) : IMediatorRequest<ResendTenantInvitationOutcome>, ITenantMemberManageOperation
{
    public string TenantId => Invitation.TenantId;
}

public union ResendTenantInvitationOutcome(
    InvitationDto,
    InvitationNotFound,
    InvitationRevisionConflict,
    InvitationForbidden,
    InvitationUnavailable,
    InvitationEncryptionUnavailable);

public sealed class ResendTenantInvitationValidator : AbstractValidator<ResendTenantInvitationRequest>
{
    public ResendTenantInvitationValidator()
    {
        RuleFor(request => request.Invitation.TenantId).Must(value => ObjectId.TryParse(value, out _));
        RuleFor(request => request.Invitation.InvitationId).InvitationId();
        RuleFor(request => request.Invitation.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class ResendTenantInvitationHandler(
    ManageInvitation management
) : IMediatorOutcomeHandler<ResendTenantInvitationRequest, ResendTenantInvitationOutcome>
{
    public Task<ResendTenantInvitationOutcome> HandleAsync(
        ResendTenantInvitationRequest request,
        CancellationToken cancellationToken = default) => management.ResendAsync(request.Invitation, cancellationToken);
}

public static class ResendTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapResendTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{tenantId}/invitations/{invitationId}/resend",
                async (
                    string tenantId, string invitationId,
                    [FromBody] InvitationRevisionBody body, [FromClaim(Claims.UserId)] ClaimValue currentUserId,
                    IMediator mediator, CancellationToken ct) =>
                {
                    var result = await mediator.Send(
                        new ResendTenantInvitationRequest(
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
                        InvitationEncryptionUnavailable error => InvitationHttpProblems.EncryptionUnavailable(error),
                    });
                })
            .WithName("ResendTenantInvitation");
        return app;
    }
}
