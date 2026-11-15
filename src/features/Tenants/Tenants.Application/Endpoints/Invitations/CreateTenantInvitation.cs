using FluentValidation;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Mediator;
using static Pole.Features.Tenants.Application.TenantMemberAuthorizationHttpProblemMappings;

namespace Pole.Features.Tenants.Application.Endpoints.Invitations;

public sealed record CreateTenantInvitationBody(
    string Email,
    TenantRole Role
);

public sealed record CreateTenantInvitationRequest(
    string TenantId,
    CreateTenantInvitationBody Body,
    InvitationActor Actor
) : IMediatorRequest<CreateTenantInvitationOutcome>, ITenantMemberRoleOperation
{
    public TenantRole Role => Body.Role;
}

public union CreateTenantInvitationOutcome(
    InvitationDto,
    TenantNotFound,
    InvitationForbidden,
    InvitationAlreadyPending,
    InvitationRecipientAlreadyMember,
    InvitationEncryptionUnavailable);

public sealed class CreateTenantInvitationHandler(
    PersistTenantInvitation persist
) : IMediatorOutcomeHandler<CreateTenantInvitationRequest, CreateTenantInvitationOutcome>
{
    public async Task<CreateTenantInvitationOutcome> HandleAsync(
        CreateTenantInvitationRequest request,
        CancellationToken cancellationToken = default)
    {
        return await persist(
            new CreateTenantInvitation(request.TenantId, request.Body.Email, request.Body.Role, request.Actor),
            cancellationToken);
    }
}

public sealed class CreateTenantInvitationValidator : AbstractValidator<CreateTenantInvitationRequest>
{
    public CreateTenantInvitationValidator()
    {
        RuleFor(request => request.TenantId).Must(value => ObjectId.TryParse(value, out _));
        RuleFor(request => request.Body.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(request => request.Body.Role).IsInEnum();
    }
}

public static class CreateTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapCreateTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{tenantId}/invitations",
                async (
                    string tenantId, [FromBody] CreateTenantInvitationBody body,
                    [FromClaim(Claims.UserId)] ClaimValue currentUserId, IMediator mediator,
                    CancellationToken ct) =>
                {
                    var result = await mediator.Send(
                        new CreateTenantInvitationRequest(tenantId, body, new InvitationActor(currentUserId)),
                        ct);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        InvitationDto invitation => Results.Created(
                            $"/api/v1/tenants/{tenantId}/invitations/{invitation.Id}",
                            invitation),
                        TenantNotFound error => MapTenantNotFound(error),
                        InvitationForbidden error => InvitationHttpProblems.Forbidden(error),
                        InvitationAlreadyPending error => InvitationHttpProblems.Pending(error),
                        InvitationRecipientAlreadyMember error => InvitationHttpProblems.AlreadyMember(error),
                        InvitationEncryptionUnavailable error => InvitationHttpProblems.EncryptionUnavailable(error),
                    });
                })
            .WithName("CreateTenantInvitation");
        return app;
    }
}
