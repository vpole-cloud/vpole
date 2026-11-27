// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.SourceBuilds.Application.Endpoints.Configurations;
using Pole.Features.SourceBuilds.Application.Endpoints.Jobs;

namespace Pole.Features.SourceBuilds.Application.Common;

public static class SourceBuildEndpoints
{
    public static IEndpointRouteBuilder MapSourceBuildEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/source-build-configurations")
            .WithTags("SourceBuildConfigurations")
            .MapGetSourceBuildConfigurationsEndpoint()
            .MapCreateSourceBuildConfigurationEndpoint()
            .MapUpdateSourceBuildConfigurationEndpoint()
            .MapDeleteSourceBuildConfigurationEndpoint()
            .MapManualSourceBuildEndpoint();

        app.MapGroup("/source-build-jobs")
            .WithTags("SourceBuildJobs")
            .MapGetSourceBuildJobsEndpoint()
            .MapGetSourceBuildJobEndpoint()
            .MapGetSourceBuildLogsEndpoint();

        return app;
    }
}
