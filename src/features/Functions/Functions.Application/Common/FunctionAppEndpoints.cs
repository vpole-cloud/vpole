using Pole.Features.Functions.Application.Mongo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.Functions.Application.Endpoints;

namespace Pole.Features.Functions.Application.Common;

public static class FunctionAppEndpoints
{
    public const string Route = "/function-apps";

    public static IEndpointRouteBuilder MapFunctionAppEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup(Route)
            .WithTags("FunctionApps")
            .MapCreateFunctionAppEndpoint()
            .MapUpdateFunctionAppEndpoint()
            .MapDeleteFunctionAppEndpoint()
            .MapGetFunctionAppByIdEndpoint()
            .MapGetFunctionAppsEndpoint()
            .MapGetFunctionRevisionsEndpoint()
            .MapGetFunctionInvocationsEndpoint()
            .MapRetryFunctionInvocationEndpoint()
            .MapGetFunctionUsageEndpoint();
        return app;
    }
}

public static class FunctionAppFeature;
