using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Endpoints;

namespace Pole.Features.ResourceDeployments.Application.Common;

public static class ResourceDeploymentEndpoints
{
    public const string Route = "/resource-deployments";

    public static IEndpointRouteBuilder MapResourceDeploymentEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGroup(Route)
                .WithMetadata(new TenantEndpointMetadata())
            .WithTags("ResourceDeployments")
            .MapRetryResourceDeploymentEndpoint();

        return app;
    }
}
