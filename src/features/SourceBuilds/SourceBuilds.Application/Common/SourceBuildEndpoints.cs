using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Endpoints;
using Pole.Features.SourceBuilds.Application.Endpoints.Configurations;
using Pole.Features.SourceBuilds.Application.Endpoints.Jobs;

namespace Pole.Features.SourceBuilds.Application.Common;

public static class SourceBuildEndpoints
{
    public static IEndpointRouteBuilder MapSourceBuildEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/source-build-configurations")
                .WithMetadata(new TenantEndpointMetadata())
            .WithTags("SourceBuildConfigurations")
            .MapGetSourceBuildConfigurationsEndpoint()
            .MapCreateSourceBuildConfigurationEndpoint()
            .MapUpdateSourceBuildConfigurationEndpoint()
            .MapDeleteSourceBuildConfigurationEndpoint()
            .MapManualSourceBuildEndpoint();

        app.MapGroup("/source-build-jobs")
                .WithMetadata(new TenantEndpointMetadata())
            .WithTags("SourceBuildJobs")
            .MapGetSourceBuildJobsEndpoint()
            .MapGetSourceBuildJobEndpoint();

        return app;
    }
}
