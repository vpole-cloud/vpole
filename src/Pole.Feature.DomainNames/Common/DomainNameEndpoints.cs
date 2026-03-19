using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Feature.DomainNames.Endpoints;

namespace Pole.Feature.DomainNames.Common;

public static class DomainNameEndpoints
{
    public const string Route = "/domain-names";

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapDomainNameEndpoints()
        {
            app.MapGroup(Route)
                .WithTags("DomainNames")
                .MapGetDomainNamesEndpoint()
                .MapGetDomainNameByIdEndpoint();

            return app;
        }
    }
}
