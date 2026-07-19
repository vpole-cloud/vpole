using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.ContainerRegistries.Application.Endpoints;

namespace Pole.Features.ContainerRegistries.Application.Common;

public static class ContainerRegistryFeature;

public static class ContainerRegistryEndpoints
{
    public static IEndpointRouteBuilder MapContainerRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/platform/encryption")
            .WithTags("PlatformEncryption")
            .MapRewrapRegistryCredentialsEndpoint();
        return app;
    }
}
