using FluentValidation;
using MongoDB.Bson;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints.Invitations;

public sealed record UpdateTenantInvitationBody(
    TenantRole Role
);

public sealed record UpdateTenantInvitationRequest(
    ManageInvitationRequest Invitation,
    TenantRole Role
) : IMediatorRequest<UpdateTenantInvitationOutcome>, ITenantMemberRoleOperation
{
    public string TenantId => Invitation.TenantId;
    public string? MemberId => null;
}

public union UpdateTenantInvitationOutcome(
    InvitationDto,
    InvitationNotFound,
    InvitationForbidden,
    InvitationUnavailable,
    InvitationEncryptionUnavailable);

public sealed class UpdateTenantInvitationValidator : AbstractValidator<UpdateTenantInvitationRequest>
{
    public UpdateTenantInvitationValidator()
    {
        RuleFor(request => request.Role).IsInEnum();
        RuleFor(request => request.Invitation.TenantId).Must(value => ObjectId.TryParse(value, out _));
        RuleFor(request => request.Invitation.InvitationId).NotEmpty().Matches("^[0-9A-F]{64}$");
    }
}

public sealed class UpdateTenantInvitationHandler(
    ManageInvitation management
) : IMediatorOutcomeHandler<UpdateTenantInvitationRequest, UpdateTenantInvitationOutcome>
{
    public async Task<UpdateTenantInvitationOutcome> HandleAsync(
        UpdateTenantInvitationRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await management.RotateAsync(request.Invitation, request.Role, cancellationToken);
        return result switch
        {
            InvitationDto invitation => invitation,
            InvitationNotFound error => error,
            InvitationForbidden error => error,
            InvitationUnavailable error => error,
            InvitationEncryptionUnavailable error => error,
        };
    }
}

public static class UpdateTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapUpdateTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut(
                "/{tenantId}/invitations/{invitationId}",
                async (
                    string tenantId, string invitationId,
                    [FromBody] UpdateTenantInvitationBody body, HttpContext context,
                    IMediator mediator, CancellationToken ct) =>
                {
                    var result = await mediator.Send(
                        new UpdateTenantInvitationRequest(
                            new ManageInvitationRequest(tenantId, invitationId, InvitationActor.From(context)),
                            body.Role),
                        ct);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        InvitationDto invitation => Results.Ok(invitation),
                        InvitationNotFound error => InvitationHttpProblems.NotFound(error),
                        InvitationForbidden error => InvitationHttpProblems.Forbidden(error),
                        InvitationUnavailable error => InvitationHttpProblems.Unavailable(error),
                        InvitationEncryptionUnavailable error => InvitationHttpProblems.EncryptionUnavailable(error),
                    });
                })
            .WithName("UpdateTenantInvitation");
        return app;
    }
}
