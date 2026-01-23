﻿using Pole.Server.Features.DomainNames.Endpoints;

namespace Pole.Server.Features.DomainNames.Common;

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
