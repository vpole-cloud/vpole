using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Tenants.Application.Endpoints;
using Pole.Features.Tenants.Application.Endpoints.Members;

namespace Pole.Features.Tenants.Application.Common;

public static class TenantEndpoints
{
    public const string Route = "/tenants";

    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup(Route)
            .WithoutTenantContext()
            .WithTags("Tenants")
            .RequireAuthorization()
            .MapGetTenantsEndpoint()
            .MapCreateTenantEndpoint()
            .MapGetTenantMembersEndpoint()
            .MapAddTenantMemberEndpoint()
            .MapUpdateTenantMemberEndpoint()
            .MapDeleteTenantMemberEndpoint();

        return app;
    }
}
