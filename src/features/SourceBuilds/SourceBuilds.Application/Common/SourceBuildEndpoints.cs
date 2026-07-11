using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.SourceBuilds.Application.Endpoints;

namespace Pole.Features.SourceBuilds.Application.Common;

public static class SourceBuildEndpoints
{
    public static IEndpointRouteBuilder MapSourceBuildEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/source-build-configurations")
            .WithTags("SourceBuildConfigurations")
            .MapSourceBuildConfigurationEndpoints()
            .MapManualSourceBuildEndpoint();

        app.MapGroup("/source-build-jobs")
            .WithTags("SourceBuildJobs")
            .MapSourceBuildJobEndpoints();

        return app;
    }
}
