using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Feature.DnsRecords.Core.Endpoints;

namespace Pole.Feature.DnsRecords.Core.Common;

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
