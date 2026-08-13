using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Features.WebServices.Application.Operations.Deployments;

namespace Pole.Features.WebServices.Application.Common;

public static class WebServiceOperationRegistration
{
    public static IServiceCollection AddWebServiceOperations(
        this IServiceCollection services)
    {
        services.TryAddScoped<AddWebServiceDeploymentOperation>();
        return services;
    }
}
