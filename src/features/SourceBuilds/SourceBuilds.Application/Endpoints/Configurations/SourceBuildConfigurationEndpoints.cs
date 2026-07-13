using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Mediator;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public static class SourceBuildConfigurations
{
    public static IEndpointRouteBuilder MapSourceBuildConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetSourceBuildConfigurationsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetSourceBuildConfigurations")
            .Produces<IReadOnlyList<SourceBuildConfigurationDto>>();

        app.MapPost("/", async (IMediator mediator, [AsParameters] CreateSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(value => Results.Created(
                    $"/api/v1/source-build-configurations/{value.Id}",
                    value));
            })
            .WithName("CreateSourceBuildConfiguration")
            .Produces<SourceBuildConfigurationDto>(StatusCodes.Status201Created);

        app.MapPut("/{ConfigurationId}", async (IMediator mediator,
                [AsParameters] UpdateSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("UpdateSourceBuildConfiguration")
            .Produces<SourceBuildConfigurationDto>();

        app.MapDelete("/{ConfigurationId}", async (IMediator mediator,
                [AsParameters] DeleteSourceBuildConfigurationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteSourceBuildConfiguration")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}
