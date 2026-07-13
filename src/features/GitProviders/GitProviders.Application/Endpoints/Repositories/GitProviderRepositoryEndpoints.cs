using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.GitProviders.Application.Common;
using Pole.Mediator;

namespace Pole.Features.GitProviders.Application.Endpoints;

public static class GitProviderRepositories
{
    public static IEndpointRouteBuilder MapGitProviderRepositoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{ConnectionId}/repositories", async (IMediator mediator, [AsParameters] GetProviderRepositoriesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetProviderRepositories")
            .Produces<ProviderPage<ProviderRepositoryDto>>();

        app.MapGet("/{ConnectionId}/repositories/{RepositoryId}/branches", async (IMediator mediator, [AsParameters] GetProviderBranchesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetProviderRepositoryBranches")
            .Produces<ProviderPage<ProviderBranchDto>>();

        app.MapPost("/{ConnectionId}/repositories/{RepositoryId}/analysis", async (IMediator mediator, [AsParameters] AnalyzeProviderRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("AnalyzeProviderRepository")
            .Produces<RepositoryAnalysisDto>();

        app.MapPost("/{ConnectionId}/repositories/{RepositoryId}/links", async (IMediator mediator, [AsParameters] LinkProviderRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(item => Results.Created($"/api/v1/source-repositories/{item.Id}", item));
            })
            .WithName("LinkProviderRepository")
            .Produces<SourceRepositoryDto>(StatusCodes.Status201Created);

        return app;
    }
}
