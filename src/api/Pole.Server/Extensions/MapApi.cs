using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Functions.Application.Common;
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
        app.MapPolePlatformEndpoints();
        app.MapGroup("/api/v1").RequireTenantContext().RequireAuthorization()
            .MapFunctionAppEndpoints()
            .MapWebServiceEndpoints()
            .MapRedisServiceEndpoints()
            .MapResourceConnectionEndpoints()
            .MapPostgresClusterEndpoints();
        return app;
    }
}
