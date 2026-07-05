using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.Features.WebServices.Application.Endpoints;

namespace Pole.Features.WebServices.Application.Common;

public static class WebServiceEndpoints
{
    public const string Route = "/web-services";

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapWebServiceEndpoints()
        {
            app.MapGroup(Route)
                .WithTags("WebServices")
                .MapCreateWebServiceEndpoint()
                .MapUpdateWebServiceEndpoint()
                .MapDeleteWebServiceEndpoints()
                .MapGetWebServiceByIdEndpoint()
                .MapGetWebServicesEndpoint();
                
            return app;
        }
    }
    
}
