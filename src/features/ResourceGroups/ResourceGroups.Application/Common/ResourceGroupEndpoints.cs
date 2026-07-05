using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.ResourceGroups.Application.Endpoints;

namespace Pole.Features.ResourceGroups.Application.Common;

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
                .MapUpdateResourceGroupEndpoint()
                .MapDeleteResourceGroupEndpoints();
                
            return app;
        }
    }
    
}

