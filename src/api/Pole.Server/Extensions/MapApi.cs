using Microsoft.AspNetCore.Routing;
using Pole.Platform.Hosting;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.WebServices.Application.Common;
namespace Pole.Server.Extensions;
public static class MapApi
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPolePlatformEndpoints();
        app.MapGroup("/api/v1").RequireAuthorization()
            .MapFunctionAppEndpoints()
            .MapWebServiceEndpoints()
            .MapRedisServiceEndpoints()
            .MapPostgresClusterEndpoints();
        return app;
    }
}
