using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Request;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Mediator;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public static class SourceBuildJobs
{
    public static IEndpointRouteBuilder MapManualSourceBuildEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{ConfigurationId}/builds", async (IMediator mediator,
                [AsParameters] StartManualSourceBuildRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(value => Results.Accepted(value: value));
            })
            .WithName("StartManualSourceBuild")
            .Produces<SourceBuildJobDto>(StatusCodes.Status202Accepted);
        return app;
    }

    public static IEndpointRouteBuilder MapSourceBuildJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetSourceBuildJobsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetSourceBuildJobs")
            .Produces<PaginatedResponse<SourceBuildJobDto>>();

        app.MapGet("/{BuildJobId}", async (IMediator mediator, [AsParameters] GetSourceBuildJobRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetSourceBuildJob")
            .Produces<SourceBuildJobDto>();
        return app;
    }
}
