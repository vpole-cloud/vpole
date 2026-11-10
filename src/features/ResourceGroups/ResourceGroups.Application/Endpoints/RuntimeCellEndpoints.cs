using Microsoft.AspNetCore.Routing;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class RuntimeCellEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeCellEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGetRuntimeCellsEndpoint().MapGetRuntimeCellEndpoint().MapRetryRuntimeCellEndpoint();
}
