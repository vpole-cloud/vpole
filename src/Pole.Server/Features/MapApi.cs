using Pole.Server.Features.ResourceGroups.Common;
using Pole.Server.Features.WebServices.Common;
using Pole.Server.Features.DomainNames.Common;

namespace Pole.Server.Features;

public static class MapApi
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1")
            .MapResourceGroupEndpoints()
            .MapWebServiceEndpoints()
            .MapDomainNameEndpoints();
        return app;
    }
    
}