using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Users.Application.Endpoints;

namespace Pole.Features.Users.Application.Common;

public static class UserEndpoints
{
    public const string Route = "/users";

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup(Route)
            .WithTags("Users")
            .RequireAuthorization()
            .MapCreateUserEndpoint();

        return app;
    }
}

