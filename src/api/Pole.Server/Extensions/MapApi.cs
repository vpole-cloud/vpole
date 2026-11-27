// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Backups.Application;
using Pole.ASP.Helpers;
using Pole.Features.Functions.Application.Common;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Platform.Hosting;

namespace Pole.Server.Extensions;

public static class MapApi
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGitHubCallbackEndpoints();
        app.MapPolePlatformEndpoints();
        app.MapGroup("/api/v1")
            .RequireTenantContext()
            .RequireAuthorization()
            .MapFunctionAppEndpoints()
            .MapWebServiceEndpoints()
            .MapBackupsEndpoints()
            .MapRedisServiceEndpoints()
            .MapResourceConnectionEndpoints()
            .MapPostgresClusterEndpoints();
        return app;
    }
}
