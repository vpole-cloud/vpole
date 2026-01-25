using Pole.Server.Features.ResourceGroups.Endpoints;

namespace Pole.Server.Features.ResourceGroups.Common;

public static class ResourceGroupEndpoints
{
    public const string Route = "/resource-groups";

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapResourceGroupEndpoints()
        {
            app.MapGroup(Route)
                .WithTags("ResourceGroups")
                .MapCreateResourceGroupEndpoint()
                .MapGetResourceGroupsEndpoint()
                .MapDeleteResourceGroupEndpoints();
                
            return app;
        }
    }
    
}