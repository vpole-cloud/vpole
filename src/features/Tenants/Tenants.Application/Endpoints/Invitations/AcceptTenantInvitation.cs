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

public sealed record InvitationRecipient(
    string Subject,
    string? Email,
    bool EmailVerified,
    string? FirstName,
    string? LastName
);

public sealed class InvitationRecipientValidator : AbstractValidator<InvitationRecipient>
{
    public InvitationRecipientValidator()
    {
        RuleFor(recipient => recipient.Subject).NotEmpty();
        RuleFor(recipient => recipient.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(recipient => recipient.EmailVerified).Equal(true);
    }
}

public sealed record AcceptTenantInvitationRequest(
    string Token,
    InvitationRecipient Recipient
) : IMediatorRequest<AcceptTenantInvitationHttpOutcome>
{
    public override string ToString() => "AcceptTenantInvitationRequest(redacted)";
}

public union AcceptTenantInvitationHttpOutcome(
    InvitationAcceptedDto,
    InvitationUnavailable,
    InvitationIdentityMismatch,
    InvitationIdentityConflict);

public sealed class AcceptTenantInvitationHandler(
    PersistAcceptedTenantInvitation persist,
    IValidator<InvitationRecipient> recipientValidator
) : IMediatorOutcomeHandler<AcceptTenantInvitationRequest, AcceptTenantInvitationHttpOutcome>
{
    public async Task<AcceptTenantInvitationHttpOutcome> HandleAsync(
        AcceptTenantInvitationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!recipientValidator.Validate(request.Recipient).IsValid)
            return new InvitationIdentityMismatch();

        var recipient = request.Recipient;
        return await persist(
            new AcceptTenantInvitation(
                request.Token,
                recipient.Subject,
                recipient.Email!,
                recipient.FirstName,
                recipient.LastName),
            cancellationToken);
    }
}

public sealed class AcceptTenantInvitationValidator : AbstractValidator<AcceptTenantInvitationRequest>
{
    public AcceptTenantInvitationValidator() => RuleFor(request => request.Token).InvitationToken();
}

public static class AcceptTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapAcceptTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/tenant-invitations/accept",
                async (
                    [FromBody] InvitationTokenBody body, [FromClaim(Claims.UserId)] ClaimValue subject,
                    [FromClaim(Claims.Email)] ClaimValue<string?> email,
                    [FromClaim("email_verified")] ClaimValue<string?> emailVerified,
                    [FromClaim("given_name")] ClaimValue<string?> firstName,
                    [FromClaim("family_name")] ClaimValue<string?> lastName, IMediator mediator,
                    CancellationToken ct) =>
                {
                    var result = await mediator.Send(
                        new AcceptTenantInvitationRequest(
                            body.Token,
                            new InvitationRecipient(
                                subject,
                                email.Value,
                                string.Equals(emailVerified.Value, "true", StringComparison.OrdinalIgnoreCase),
                                firstName.Value,
                                lastName.Value)),
                        ct);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        InvitationAcceptedDto accepted => Results.Ok(accepted),
                        InvitationUnavailable error => InvitationHttpProblems.Unavailable(error),
                        InvitationIdentityMismatch error => InvitationHttpProblems.IdentityMismatch(error),
                        InvitationIdentityConflict error => InvitationHttpProblems.IdentityConflict(error),
                    });
                })
            .WithoutTenantContext()
            .RequireAuthorization()
            .WithName("AcceptTenantInvitation");
        return app;
    }
}
