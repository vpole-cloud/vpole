using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Pole.Features.GitProviders.Application.GitHub;

namespace Pole.Features.GitProviders.Application.Endpoints;

internal static class CompleteGitHubInstallationSetup
{
    internal static async Task<IResult> HandleAsync(
        IGitHubSetupService setupService,
        [FromQuery(Name = "installation_id")] string? installationId,
        [FromQuery] string? state,
        [FromQuery(Name = "setup_action")] string? setupAction,
        CancellationToken cancellationToken)
    {
        var redirect = await setupService.HandleInstallationCallbackAsync(
            installationId, state, setupAction, cancellationToken);
        return Results.Redirect(redirect);
    }
}
