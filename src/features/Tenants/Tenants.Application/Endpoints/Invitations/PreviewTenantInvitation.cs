using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application.Endpoints.Invitations;

public sealed record PreviewTenantInvitationRequest(
    InvitationTokenBody Body
) : IMediatorRequest<PreviewTenantInvitationOutcome>;

public union PreviewTenantInvitationOutcome(InvitationPreviewDto, InvitationUnavailable);

public sealed class PreviewTenantInvitationValidator : AbstractValidator<PreviewTenantInvitationRequest>
{
    public PreviewTenantInvitationValidator() =>
        RuleFor(request => request.Body.Token).NotEmpty().Matches("^[A-Za-z0-9_-]{43}$");
}

public sealed class PreviewTenantInvitationHandler(
    MongoLoadInvitation load
) : IMediatorOutcomeHandler<PreviewTenantInvitationRequest, PreviewTenantInvitationOutcome>
{
    public async Task<PreviewTenantInvitationOutcome> HandleAsync(
        PreviewTenantInvitationRequest request,
        CancellationToken cancellationToken = default)
    {
        var preview = await load.PreviewAsync(request.Body.Token, cancellationToken);
        return preview is null ? new InvitationUnavailable() : preview;
    }
}

public static class PreviewTenantInvitationEndpoint
{
    public static IEndpointRouteBuilder MapPreviewTenantInvitationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/tenant-invitations/preview",
                async (
                    [FromBody] InvitationTokenBody body, IMediator mediator,
                    CancellationToken ct) =>
                {
                    var result = await mediator.Send(new PreviewTenantInvitationRequest(body), ct);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        InvitationPreviewDto preview => Results.Ok(preview),
                        InvitationUnavailable error => InvitationHttpProblems.Unavailable(error),
                    });
                })
            .WithoutTenantContext()
            .AllowAnonymous()
            .WithName("PreviewTenantInvitation");
        return app;
    }
}
