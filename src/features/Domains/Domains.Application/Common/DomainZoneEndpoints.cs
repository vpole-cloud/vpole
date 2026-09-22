using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.Domains.Application.Endpoints;

namespace Pole.Features.Domains.Application.Common;

public static class DomainZoneEndpoints
{
    public const string Route = "/domain-zones";

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapDomainZoneEndpoints()
        {
            app.MapGroup(Route)
                .WithTags("Domain zones")
                .MapGetDomainZonesEndpoint()
                .MapGetDomainZoneEndpoint()
                .MapCreateDomainZoneEndpoint()
                .MapDeleteDomainZoneEndpoint();
            return app;
        }
    }
}
