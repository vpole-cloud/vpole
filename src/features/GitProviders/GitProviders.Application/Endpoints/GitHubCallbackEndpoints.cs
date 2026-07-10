using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.Features.GitProviders.Application.GitHub;

namespace Pole.Features.GitProviders.Application.Endpoints;

public static class GitHubCallbackEndpoints
{
    public static IEndpointRouteBuilder MapGitHubSetupCallbackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/integrations/github/setup-complete", async (
                IGitHubSetupService setupService,
                [FromQuery(Name = "installation_id")] string? installationId,
                [FromQuery] string? state,
                [FromQuery(Name = "setup_action")] string? setupAction,
                CancellationToken cancellationToken) =>
            {
                var redirect = await setupService.HandleInstallationCallbackAsync(
                    installationId,
                    state,
                    setupAction,
                    cancellationToken);
                return Results.Redirect(redirect);
            })
            .AllowAnonymous()
            .WithName("CompleteGitHubInstallationSetup");

        app.MapGet("/api/v1/integrations/github/oauth-callback", async (
                IGitHubSetupService setupService,
                [FromQuery] string? code,
                [FromQuery] string? state,
                CancellationToken cancellationToken) =>
            {
                var redirect = await setupService.HandleOAuthCallbackAsync(code, state, cancellationToken);
                return Results.Redirect(redirect);
            })
            .AllowAnonymous()
            .WithName("CompleteGitHubOAuthVerification");

        return app;
    }
}
