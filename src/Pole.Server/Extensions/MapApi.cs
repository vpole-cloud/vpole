using Pole.Feature.DomainNames.Common;
using Pole.Features.Postgres.Endpoints;
using Pole.Features.RedisServices.Common;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.WebServices.Common;

namespace Pole.Server.Extensions;

public static class MapApi
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1");

        group.MapResourceGroupEndpoints()
            .MapWebServiceEndpoints()
            .MapRedisServiceEndpoints()
            .MapPostgresClusterEndpoints()
            .MapDomainNameEndpoints();

        return app;
    }
    
}
