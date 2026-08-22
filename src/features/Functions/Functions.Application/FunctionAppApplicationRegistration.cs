using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.ASP.Helpers;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence;

namespace Pole.Features.Functions.Application;

public static class FunctionAppApplicationRegistration
{
    public static IServiceCollection AddFunctionAppsFeature(
        this IServiceCollection services)
    {
        services.AddHttpProblemMappings(FunctionAppHttpMappings.All);
        services.AddFunctionAppPersistence();
        services.TryAddScoped<IFunctionRevisionLifecycleService, FunctionRevisionLifecycleService>();
        return services;
    }
}
