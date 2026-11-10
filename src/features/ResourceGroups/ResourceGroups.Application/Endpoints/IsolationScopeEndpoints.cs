using Microsoft.AspNetCore.Routing;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class IsolationScopeEndpoints
{
    public static IEndpointRouteBuilder MapIsolationScopeEndpoints(this IEndpointRouteBuilder app) => app
        .MapCreateIsolationScopeEndpoint()
        .MapGetIsolationScopesEndpoint()
        .MapGetIsolationScopeEndpoint()
        .MapDeleteIsolationScopeEndpoint();
}
