using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence.Repositories;
using Pole.Mediator;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitProviders.Application.Endpoints.SourceRepositories;

public sealed record GetSourceRepositoriesRequest(
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetSourceRepositoriesOutcome>, ITenantReadOperation;

public union GetSourceRepositoriesOutcome(IReadOnlyList<SourceRepositoryDto>);

public sealed class GetSourceRepositoriesHandler(LoadSourceRepositories loadSourceRepositories, IGitOpsMutations git)
    : IMediatorOutcomeHandler<GetSourceRepositoriesRequest, GetSourceRepositoriesOutcome>
{
    public async Task<GetSourceRepositoriesOutcome> HandleAsync(
        GetSourceRepositoriesRequest request,
        CancellationToken cancellationToken = default)
    {
        var repositories = await loadSourceRepositories(request.TenantId, cancellationToken);
        var statuses = await git.StatusesAsync(new(request.TenantId, "SourceRepository",
            repositories.Select(item => item.Id).ToArray()), cancellationToken);
        return repositories.Select(item => item.ToDto() with
        { GitOps = statuses.GetValueOrDefault(item.Id) }).ToArray();
    }
}

public static class GetSourceRepositoriesEndpoint
{
    public static IEndpointRouteBuilder MapGetSourceRepositoriesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetSourceRepositoriesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyList<SourceRepositoryDto> repositories => Results.Ok(repositories)
                });
            })
            .WithName("GetSourceRepositories")
            .Produces<IReadOnlyList<SourceRepositoryDto>>();
        return app;
    }
}
