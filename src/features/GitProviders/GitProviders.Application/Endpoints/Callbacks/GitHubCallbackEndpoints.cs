using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Pole.Features.GitProviders.Application.Endpoints;

public static class GitHubCallbackEndpoints
{
    public static IEndpointRouteBuilder MapGitHubSetupCallbackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/integrations/github/setup-complete", CompleteGitHubInstallationSetup.HandleAsync)
            .AllowAnonymous()
            .WithName("CompleteGitHubInstallationSetup");

        app.MapGet("/api/v1/integrations/github/oauth-callback", CompleteGitHubOAuthVerification.HandleAsync)
            .AllowAnonymous()
            .WithName("CompleteGitHubOAuthVerification");

        return app;
    }
}
