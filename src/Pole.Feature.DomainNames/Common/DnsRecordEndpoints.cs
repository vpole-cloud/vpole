using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Feature.DomainNames.Endpoints;

namespace Pole.Feature.DomainNames.Common;

public static class DnsRecordEndpoints
{
    public const string Route = "/dns-records";

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapDomainNameEndpoints()
        {
            app.MapGroup(Route)
                .WithTags("DnsRecord")
                .MapGetDomainNamesEndpoint()
                .MapGetDomainNameByIdEndpoint();

            return app;
        }
    }
}
