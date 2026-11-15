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
    string Email,
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
    [FromBody] InvitationTokenBody Body,
    [FromClaim(Claims.UserId)] ClaimValue Subject,
    [FromClaim(Claims.Email)] ClaimValue<string?> Email,
    [FromClaim("email_verified")] ClaimValue<string?> EmailVerified,
    [FromClaim("given_name")] ClaimValue<string?> FirstName,
    [FromClaim("family_name")] ClaimValue<string?> LastName
) : IMediatorRequest<AcceptTenantInvitationHttpOutcome>
{
    public InvitationRecipient Recipient =>
        new(
            Subject,
            Email.Value ?? string.Empty,
            string.Equals(EmailVerified.Value, "true", StringComparison.OrdinalIgnoreCase),
            FirstName.Value,
            LastName.Value);

    public override string ToString() => "AcceptTenantInvitationRequest(redacted)";
}

public union AcceptTenantInvitationHttpOutcome(
    InvitationAcceptedDto,
    InvitationUnavailable,
    InvitationIdentityMismatch,
    InvitationIdentityConflict);

public sealed class InvitationRecipientAuthorizationBehavior(
    IValidator<InvitationRecipient> validator
) : IPipelineBehavior<AcceptTenantInvitationRequest, AcceptTenantInvitationHttpOutcome>
{
    public async Task<MediatorResult<AcceptTenantInvitationHttpOutcome>> Handle(
        AcceptTenantInvitationRequest request,
        RequestHandlerDelegate<AcceptTenantInvitationHttpOutcome> next,
        CancellationToken cancellationToken)
    {
        if (!(await validator.ValidateAsync(request.Recipient, cancellationToken)).IsValid)
            return new PipelineRejected(new InvitationIdentityMismatch());

        return await next();
    }
}

public sealed class AcceptTenantInvitationHandler(
    PersistAcceptedTenantInvitation persist
) : IMediatorOutcomeHandler<AcceptTenantInvitationRequest, AcceptTenantInvitationHttpOutcome>
{
    public Task<AcceptTenantInvitationHttpOutcome> HandleAsync(
        AcceptTenantInvitationRequest request,
        CancellationToken cancellationToken = default)
    {
        var recipient = request.Recipient;
        return persist(
            new AcceptTenantInvitation(
                request.Body.Token,
                recipient.Subject,
                recipient.Email,
                recipient.FirstName,
                recipient.LastName),
            cancellationToken);
    }
}

public sealed class AcceptTenantInvitationValidator : AbstractValidator<AcceptTenantInvitationRequest>
{
    public AcceptTenantInvitationValidator() => RuleFor(request => request.Body.Token)
        .InvitationToken()
        .OverridePropertyName(nameof(InvitationTokenBody.Token));
}

public static class AcceptTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapAcceptTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/tenant-invitations/accept",
                async (
                    [AsParameters] AcceptTenantInvitationRequest request, IMediator mediator,
                    CancellationToken ct) =>
                {
                    var result = await mediator.Send(request, ct);
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
