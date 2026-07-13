using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.GitProviders.Application.Common;
using Pole.Mediator;

namespace Pole.Features.GitProviders.Application.Endpoints;

public static class SourceRepositories
{
    public static IEndpointRouteBuilder MapSourceRepositoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetSourceRepositoriesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetSourceRepositories")
            .Produces<IReadOnlyList<SourceRepositoryDto>>();

        app.MapPost("/{Id}/reanalyze", async (IMediator mediator, [AsParameters] ReanalyzeSourceRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("ReanalyzeSourceRepository")
            .Produces<SourceRepositoryDto>();

        app.MapDelete("/{Id}", async (IMediator mediator, [AsParameters] DeleteSourceRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteSourceRepository")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}
