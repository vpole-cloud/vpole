using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Pole.Features.GitProviders.Application.GitHub;

namespace Pole.Features.GitProviders.Application.Endpoints;

internal static class CompleteGitHubOAuthVerification
{
    internal static async Task<IResult> HandleAsync(
        IGitHubSetupService setupService,
        [FromQuery] string? code,
        [FromQuery] string? state,
        CancellationToken cancellationToken)
    {
        var redirect = await setupService.HandleOAuthCallbackAsync(code, state, cancellationToken);
        return Results.Redirect(redirect);
    }
}
