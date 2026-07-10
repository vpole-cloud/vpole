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
                .MapGitProviderConnectionEndpoints()
                .MapGitProviderRepositoryEndpoints();

            app.MapGroup("/source-repositories")
                .WithTags("SourceRepositories")
                .MapSourceRepositoryEndpoints();

            return app;
        }

        public IEndpointRouteBuilder MapGitHubCallbackEndpoints()
        {
            app.MapGitHubSetupCallbackEndpoints();
            return app;
        }
    }
}
