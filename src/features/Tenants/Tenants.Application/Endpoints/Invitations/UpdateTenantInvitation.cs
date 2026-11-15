using System.Text.Json.Serialization;
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

public sealed record UpdateTenantInvitationBody(
    [property: JsonRequired] TenantRole Role,
    long ExpectedRevision
);

public sealed record UpdateTenantInvitationRequest(
    [FromRoute] string TenantId,
    [FromRoute] string InvitationId,
    [FromBody] UpdateTenantInvitationBody Body,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId
) : IMediatorRequest<UpdateTenantInvitationOutcome>, ITenantMemberRoleOperation
{
    public TenantRole Role => Body.Role;
}

public union UpdateTenantInvitationOutcome(
    InvitationDto,
    InvitationNotFound,
    InvitationRevisionConflict,
    InvitationForbidden,
    InvitationUnavailable,
    InvitationEncryptionUnavailable);

public sealed class UpdateTenantInvitationValidator : AbstractValidator<UpdateTenantInvitationRequest>
{
    public UpdateTenantInvitationValidator()
    {
        RuleFor(request => request.Body.Role).IsInEnum();
        RuleFor(request => request.TenantId).RouteObjectId();
        RuleFor(request => request.InvitationId).InvitationId();
        RuleFor(request => request.Body.ExpectedRevision).GreaterThan(0);
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
        var result = await management.RotateAsync(
            new ManageInvitationRequest(
                request.TenantId,
                request.InvitationId,
                request.Body.ExpectedRevision,
                new InvitationActor(request.CurrentUserId)),
            request.Role,
            cancellationToken);
        return result switch
        {
            InvitationDto invitation => invitation,
            InvitationNotFound error => error,
            InvitationRevisionConflict error => error,
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
                "/{TenantId}/invitations/{InvitationId}",
                async (
                    [AsParameters] UpdateTenantInvitationRequest request, IMediator mediator,
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
                        InvitationEncryptionUnavailable error => InvitationHttpProblems.EncryptionUnavailable(error),
                    });
                })
            .WithName("UpdateTenantInvitation");
        return app;
    }
}
