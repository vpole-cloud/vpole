using Pole.Feature.DnsRecords.Core.Common;
using Pole.Features.Postgres.Endpoints;
using Pole.Features.RedisServices.Common;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.Search.Common;
using Pole.Features.Tenants.Common;
using Pole.Features.Users.Common;
using Pole.Features.WebServices.Common;

namespace Pole.Server.Extensions;

public static class MapApi
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .RequireAuthorization();

        group.MapResourceGroupEndpoints()
            .MapTenantEndpoints()
            .MapUserEndpoints()
            .MapWebServiceEndpoints()
            .MapRedisServiceEndpoints()
            .MapPostgresClusterEndpoints()
            .MapSearchEndpoints()
            .MapDomainNameEndpoints();

        return app;
    }
    
}
