using Pole.Server.Api.WebServices.Endpoints;

namespace Pole.Server.Api.WebServices;

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
                .MapGetWebServiceByIdEndpoint()
                .MapGetWebServicesEndpoint();
                
            return app;
        }
    }
    
}