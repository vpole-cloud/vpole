using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints.Invitations;

public sealed record InvitationTokenBody(
    string Token
)
{
    public override string ToString() => "InvitationTokenBody(redacted)";
}

public sealed record AcceptTenantInvitationRequest(
    AcceptTenantInvitation Invitation
) : IMediatorRequest<AcceptTenantInvitationHttpOutcome>;

public union AcceptTenantInvitationHttpOutcome(
    InvitationAcceptedDto,
    InvitationUnavailable,
    InvitationIdentityMismatch);

public sealed class AcceptTenantInvitationHandler(
    PersistAcceptedTenantInvitation persist
) : IMediatorOutcomeHandler<AcceptTenantInvitationRequest, AcceptTenantInvitationHttpOutcome>
{
    public async Task<AcceptTenantInvitationHttpOutcome> HandleAsync(
        AcceptTenantInvitationRequest request,
        CancellationToken cancellationToken = default)
    {
        return await persist(request.Invitation, cancellationToken);
    }
}

public sealed class AcceptTenantInvitationValidator : AbstractValidator<AcceptTenantInvitationRequest>
{
    public AcceptTenantInvitationValidator() =>
        RuleFor(request => request.Invitation.Token).NotEmpty().Matches("^[A-Za-z0-9_-]{43}$");
}

public static class AcceptTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapAcceptTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/tenant-invitations/accept",
                async (
                    [FromBody] InvitationTokenBody body, HttpContext context,
                    IMediator mediator, CancellationToken ct) =>
                {
                    var user = context.User;
                    var result = await mediator.Send(
                        new AcceptTenantInvitationRequest(
                            new AcceptTenantInvitation(
                                body.Token,
                                user.FindFirstValue(Claims.UserId) ?? "",
                                user.FindFirstValue(Claims.Email) ?? "",
                                string.Equals(
                                    user.FindFirstValue("email_verified"),
                                    "true",
                                    StringComparison.OrdinalIgnoreCase),
                                user.FindFirstValue("given_name"),
                                user.FindFirstValue("family_name"))),
                        ct);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        InvitationAcceptedDto accepted => Results.Ok(accepted),
                        InvitationUnavailable error => InvitationHttpProblems.Unavailable(error),
                        InvitationIdentityMismatch error => InvitationHttpProblems.IdentityMismatch(error),
                    });
                })
            .WithoutTenantContext()
            .RequireAuthorization()
            .WithName("AcceptTenantInvitation");
        return app;
    }
}
