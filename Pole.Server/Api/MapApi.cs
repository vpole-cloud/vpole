using Pole.Server.Api.WebServices;

namespace Pole.Server.Api;

public static class MapApi
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1")
            .MapWebServiceEndpoints();
        return app;
    }
    
}