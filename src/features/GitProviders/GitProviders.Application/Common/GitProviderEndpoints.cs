using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.GitProviders.Application.Endpoints;

namespace Pole.Features.GitProviders.Application.Common;

public static class GitProviderEndpoints
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGitProviderEndpoints()
        {
            app.MapGroup("/git-provider-connections")
                .WithTags("GitProviderConnections")
                .MapGetGitProviderConnectionsEndpoint()
                .MapBeginGitHubInstallationEndpoint()
                .MapDeleteGitProviderConnectionEndpoint()
                .MapGetProviderRepositoriesEndpoint()
                .MapGetProviderBranchesEndpoint()
                .MapAnalyzeProviderRepositoryEndpoint()
                .MapLinkProviderRepositoryEndpoint();

            app.MapGroup("/source-repositories")
                .WithTags("SourceRepositories")
                .MapGetSourceRepositoriesEndpoint()
                .MapReanalyzeSourceRepositoryEndpoint()
                .MapDeleteSourceRepositoryEndpoint();

            return app;
        }

        public IEndpointRouteBuilder MapGitHubCallbackEndpoints()
        {
            CompleteGitHubInstallationSetup.MapEndpoint(app);
            CompleteGitHubOAuthVerification.MapEndpoint(app);
            return app;
        }
    }
}
