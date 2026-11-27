// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.Features.GitProviders.Application.GitHub;

namespace Pole.Features.GitProviders.Application.Endpoints.Callbacks;

internal static class CompleteGitHubOAuthVerification
{
    internal static IEndpointRouteBuilder MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/integrations/github/oauth-callback", HandleAsync)
            .AllowAnonymous()
            .WithName("CompleteGitHubOAuthVerification");
        return app;
    }

    internal static async Task<IResult> HandleAsync(
        GitHubSetupService setupService,
        [FromQuery] string? code,
        [FromQuery] string? state,
        CancellationToken cancellationToken)
    {
        var redirect = await setupService.HandleOAuthCallbackAsync(code, state, cancellationToken);
        return Results.Redirect(redirect);
    }
}
