using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Pole.Features.DomainNames.Common;
using Pole.Features.RedisServices.Common;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.WebServices.Common;

namespace Pole.Features;

public static class MapApi
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1")
            .MapResourceGroupEndpoints()
            .MapWebServiceEndpoints()
            .MapRedisServiceEndpoints()
            .MapDomainNameEndpoints();
        return app;
    }
    
}