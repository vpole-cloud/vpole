using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints.Invitations;

public sealed record StartInvitationOnboardingRequest(
    InvitationTokenBody Body
) : IMediatorRequest<StartInvitationOnboardingOutcome>;

public union StartInvitationOnboardingOutcome(
    InvitationOnboardingDto,
    InvitationUnavailable,
    InvitationIdentitySetupUnavailable);

public sealed class StartInvitationOnboardingValidator : AbstractValidator<StartInvitationOnboardingRequest>
{
    public StartInvitationOnboardingValidator() =>
        RuleFor(request => request.Body.Token).NotEmpty().Matches("^[A-Za-z0-9_-]{43}$");
}

public sealed class StartInvitationOnboardingHandler(
    StartInvitationOnboarding onboarding
) : IMediatorOutcomeHandler<StartInvitationOnboardingRequest, StartInvitationOnboardingOutcome>
{
    public Task<StartInvitationOnboardingOutcome> HandleAsync(
        StartInvitationOnboardingRequest request,
        CancellationToken cancellationToken = default) => onboarding.InvokeAsync(request.Body.Token, cancellationToken);
}

public static class StartInvitationOnboardingEndpoint
{
    public static IEndpointRouteBuilder MapStartInvitationOnboardingEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/tenant-invitations/onboarding",
                async (
                    [FromBody] InvitationTokenBody body, IMediator mediator,
                    CancellationToken ct) =>
                {
                    var result = await mediator.Send(new StartInvitationOnboardingRequest(body), ct);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        InvitationOnboardingDto setup => Results.Ok(setup),
                        InvitationUnavailable error => InvitationHttpProblems.Unavailable(error),
                        InvitationIdentitySetupUnavailable error => InvitationHttpProblems.IdentitySetupUnavailable(
                            error),
                    });
                })
            .WithoutTenantContext()
            .AllowAnonymous()
            .WithName("StartInvitationOnboarding");
        return app;
    }
}
