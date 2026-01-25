using Pole.Server.Features.RedisServices.Endpoints;

namespace Pole.Server.Features.RedisServices.Common;

public static class RedisServiceEndpoints
{
    public const string Route = "/redis-services";

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapRedisServiceEndpoints()
        {
            app.MapGroup(Route)
                .WithTags("RedisServices")
                .MapCreateRedisServiceEndpoint()
                .MapUpdateRedisServiceEndpoint()
                .MapDeleteRedisServiceEndpoints()
                .MapGetRedisServiceByIdEndpoint()
                .MapGetRedisServicesEndpoint();
                
            return app;
        }
    }
}
