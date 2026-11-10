using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.Features.GitProviders.Application.GitHub;

namespace Pole.Features.GitProviders.Application.Endpoints.Callbacks;

internal static class CompleteGitHubInstallationSetup
{
    internal static IEndpointRouteBuilder MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/integrations/github/setup-complete", HandleAsync)
            .AllowAnonymous()
            .WithName("CompleteGitHubInstallationSetup");
        return app;
    }

    internal static async Task<IResult> HandleAsync(
        GitHubSetupService setupService,
        [FromQuery(Name = "installation_id")] string? installationId,
        [FromQuery] string? state,
        [FromQuery(Name = "setup_action")] string? setupAction,
        CancellationToken cancellationToken)
    {
        var redirect = await setupService.HandleInstallationCallbackAsync(
            installationId,
            state,
            setupAction,
            cancellationToken);
        return Results.Redirect(redirect);
    }
}
